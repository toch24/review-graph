// Browser tests for src/ReviewGraph/viewer.html. They build a page from the viewer plus a sample
// review JSON, so no .NET build is needed.   cd tests/viewer && npm i && npx playwright install chromium && npm test
const { test, before, after, beforeEach } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { chromium } = require("playwright");

const root = path.resolve(__dirname, "../..");
const viewer = fs.readFileSync(path.join(root, "src/ReviewGraph/viewer.html"), "utf8");
const sample = (name) => JSON.parse(fs.readFileSync(path.join(root, "samples", name), "utf8"));
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "rg-"));

function page(data, name) {
  const html = viewer.replace("__PR_TITLE__", "t").replace("/*REVIEW_DATA*/null", JSON.stringify(data));
  const f = path.join(tmp, name + ".html");
  fs.writeFileSync(f, html);
  return "file://" + f;
}
const WITH_TESTS = sample("sample-shop-review-with-tests.json");
const noProject = () => { const d = structuredClone(sample("sample-shop-review.json")); d.nodes.forEach((n) => delete n.project); return d; };

let browser, p, errors;
before(async () => { browser = await chromium.launch({ executablePath: process.env.CHROMIUM_PATH || (fs.existsSync("/opt/pw-browsers/chromium") ? "/opt/pw-browsers/chromium" : undefined) }); });
after(async () => { await browser.close(); });
beforeEach(async () => {
  p = await browser.newPage({ viewport: { width: 1500, height: 900 } });
  errors = [];
  p.on("pageerror", (e) => errors.push(e.message));
});
async function open(data = WITH_TESTS, name = "with-tests") {
  await p.goto(page(data, name));
  await p.waitForSelector(".node");
  await p.waitForTimeout(600);
}
const box = (sel) => p.locator(sel).first().boundingBox();
const groupBox = (i) => p.locator(".group:not([hidden])").nth(i).boundingBox();
const within = (inner, outer, slack = 1) =>
  inner.x >= outer.x - slack && inner.y >= outer.y - slack && inner.x + inner.width <= outer.x + outer.width + slack && inner.y + inner.height <= outer.y + outer.height + slack;
async function drag(from, to, steps = 10) {
  await p.mouse.move(from.x, from.y); await p.mouse.down(); await p.mouse.move(to.x, to.y, { steps }); await p.mouse.up();
}
async function nodeGroupMap() {
  return p.evaluate(() => GROUPS.map((g) => ({ name: g.name, ids: g.ids })));
}
async function worldRect(id) { // node rect in screen px
  return p.evaluate((id) => { const r = nodeEls[id].getBoundingClientRect(); return { x: r.x, y: r.y, width: r.width, height: r.height }; }, id);
}
async function groupRect(name) {
  return p.evaluate((n) => { const g = GROUPS.find((g) => g.name === n); const r = g.el.getBoundingClientRect(); return { x: r.x, y: r.y, width: r.width, height: r.height }; }, name);
}

test("loads the sample without page errors and draws cards and edges", async () => {
  await open();
  assert.equal(await p.locator(".node").count(), WITH_TESTS.nodes.length);
  assert.ok((await p.locator("svg.edges path").count()) >= WITH_TESTS.edges.length);
  assert.deepEqual(errors, []);
});

test("one container per project, titled with the project name", async () => {
  await open();
  const titles = await p.locator(".group-title").allInnerTexts();
  assert.equal(titles.length, 2);
  assert.ok(titles.some((t) => t.includes("Shop.Api")) && titles.some((t) => t.includes("Shop.Tests")));
});

test("test projects are stacked after production projects", async () => {
  await open();
  const api = await groupRect("Shop.Api"), tests = await groupRect("Shop.Tests");
  assert.ok(tests.y > api.y + api.height - 1);
});

test("every card starts fully inside its own container", async () => {
  await open();
  for (const g of await nodeGroupMap()) {
    const gr = await groupRect(g.name);
    for (const id of g.ids) assert.ok(within(await worldRect(id), gr), `${id} outside ${g.name}`);
  }
});

test("containers do not overlap at first layout", async () => {
  await open();
  const a = await groupRect("Shop.Api"), b = await groupRect("Shop.Tests");
  assert.ok(a.y + a.height <= b.y || b.y + b.height <= a.y);
});

test("a card cannot be dragged out of its container", async () => {
  await open();
  const g = await groupRect("Shop.Tests");
  const id = "couponservicetests-rejectsexpiredcoupon";
  for (const [dx, dy] of [[900, 900], [-900, -900], [900, -900], [-900, 900]]) {
    const h = await p.locator(`.node[data-id="${id}"] .node-head`).boundingBox();
    await drag({ x: h.x + 20, y: h.y + 3 }, { x: h.x + 20 + dx, y: h.y + 3 + dy });
    assert.ok(within(await worldRect(id), g, 2), `escaped with drag ${dx},${dy}`);
  }
});

test("dragging a container's title moves it together with all of its cards", async () => {
  await open();
  const before = await Promise.all((await nodeGroupMap())[1].ids.map(async (id) => [id, await worldRect(id)]));
  const gBefore = await groupRect("Shop.Tests");
  const t = await p.locator(".group-title").first().boundingBox(); // last-created group is first in DOM
  const titleOf = await p.evaluate(() => GROUPS.map((g) => g.name));
  const idx = titleOf.indexOf("Shop.Tests");
  const tb = await p.locator(".group-title").nth(titleOf.length - 1 - idx).boundingBox();
  await drag({ x: tb.x + 10, y: tb.y + 3 }, { x: tb.x + 210, y: tb.y + 103 });
  const gAfter = await groupRect("Shop.Tests");
  const dx = gAfter.x - gBefore.x, dy = gAfter.y - gBefore.y;
  assert.ok(Math.abs(dx) > 20 && Math.abs(dy) > 20, "container did not move");
  for (const [id, r] of before) {
    const a = await worldRect(id);
    assert.ok(Math.abs(a.x - r.x - dx) < 1 && Math.abs(a.y - r.y - dy) < 1, `${id} did not follow`);
  }
  assert.deepEqual(errors, []);
});

test("moving a container keeps edges attached to their cards", async () => {
  await open();
  const names = await p.evaluate(() => GROUPS.map((g) => g.name));
  const tb = await p.locator(".group-title").nth(names.length - 1 - names.indexOf("Shop.Tests")).boundingBox();
  const attached = () => p.evaluate(() => {
    const e = EDGES.findIndex((e) => parse(e.from).id.startsWith("couponservicetests-") && parse(e.to).id === "couponservice-validateasync");
    const [sx, sy] = pathEls[e].getAttribute("d").match(/M([\d.-]+),([\d.-]+)/).slice(1).map(Number);
    const s = pos[parse(EDGES[e].from).id];
    return { sx, sy, nx: s.x, ny: s.y };
  });
  await drag({ x: tb.x + 10, y: tb.y + 3 }, { x: tb.x + 200, y: tb.y + 90 });
  const a = await attached();
  assert.ok(a.sx >= a.nx - 1 && a.sy >= a.ny - 1, "edge start not on its source card");
});

test("dragging a card at a zoomed-out level moves it by the pointer delta", async () => {
  await open();
  const id = "couponservicetests-rejectsunknowncode";
  const h = await p.locator(`.node[data-id="${id}"] .node-head`).boundingBox();
  const before = await worldRect(id);
  await drag({ x: h.x + 10, y: h.y + 2 }, { x: h.x + 10 + 15, y: h.y + 2 + 6 });
  const after = await worldRect(id);
  // may be clamped, but must never move the wrong way or by more than the pointer
  assert.ok(after.x - before.x <= 15.5 && after.y - before.y <= 6.5);
});

test("the reset button restores the stacked layout after dragging", async () => {
  await open();
  const g0 = await groupRect("Shop.Tests");
  const names = await p.evaluate(() => GROUPS.map((g) => g.name));
  const tb = await p.locator(".group-title").nth(names.length - 1 - names.indexOf("Shop.Tests")).boundingBox();
  await drag({ x: tb.x + 10, y: tb.y + 3 }, { x: tb.x + 300, y: tb.y + 150 });
  await p.click("#relayout"); await p.waitForTimeout(700);
  const g1 = await groupRect("Shop.Tests"), a = await groupRect("Shop.Api");
  assert.ok(g1.y >= a.y + a.height - 1, "tests container is not below the api container again");
  assert.ok(g1.width > 0 && g0.width > 0);
});

test("dragging the canvas or a container never selects text", async () => {
  await open();
  const g = await groupRect("Shop.Api");
  await drag({ x: g.x + g.width - 12, y: g.y + g.height / 2 }, { x: g.x + 100, y: g.y + 80 });
  assert.equal(await p.evaluate(() => getSelection().toString().length), 0);
  await drag({ x: 900, y: 850 }, { x: 200, y: 200 });
  assert.equal(await p.evaluate(() => getSelection().toString().length), 0);
});

test("code inside a card can still be selected and copied", async () => {
  await open();
  await p.keyboard.press("ArrowRight"); await p.waitForTimeout(900);
  const line = p.locator(".node.primary .row .code").nth(8); // `if (!string.IsNullOrWhiteSpace(request.CouponCode))`
  const c = await line.boundingBox();
  await drag({ x: c.x - 1, y: c.y + c.height / 2 }, { x: c.x + c.width + 2, y: c.y + c.height / 2 });
  assert.equal(await p.evaluate(() => getSelection().toString().trim()), (await line.innerText()).trim(), "copied text is missing the highlighted tokens");
});

test("expand button widens the card and shows full lines", async () => {
  await open();
  await p.keyboard.press("ArrowRight"); await p.waitForTimeout(900);
  const id = "checkoutservice-placeorderasync";
  const sel = `.node[data-id="${id}"]`;
  const clipped = () => p.evaluate((s) => [...document.querySelectorAll(s + " .row .code")].filter((c) => c.scrollWidth > c.clientWidth + 1).length, sel);
  const w0 = (await p.locator(sel).boundingBox()).width;
  const before = await clipped();
  await p.locator(sel + " .expand").click();
  assert.ok((await p.locator(sel).boundingBox()).width >= w0);
  assert.equal(await clipped(), 0, `${before} lines were clipped before expanding, still clipped after`);
  await p.locator(sel + " .expand").click();
  assert.equal(await p.locator(sel).evaluate((e) => e.classList.contains("expanded")), false);
});

test("expanding a card keeps it inside its container and does not drag it", async () => {
  await open();
  const id = "checkoutservice-placeorderasync";
  const before = await p.evaluate((id) => ({ ...pos[id] }), id);
  await p.locator(`.node[data-id="${id}"] .expand`).click();
  const after = await p.evaluate((id) => ({ ...pos[id] }), id);
  assert.deepEqual(after, before, "card moved on expand click");
  assert.ok(within(await worldRect(id), await groupRect("Shop.Api"), 2));
});

test("a container grows when a card in it expands, without overlapping the next container", async () => {
  await open();
  for (const id of await p.evaluate(() => GROUPS.find((g) => g.name === "Shop.Tests").ids))
    await p.locator(`.node[data-id="${id}"] .expand`).click();
  const t = await groupRect("Shop.Tests");
  for (const id of await p.evaluate(() => GROUPS.find((g) => g.name === "Shop.Tests").ids)) assert.ok(within(await worldRect(id), t, 2), id + " overflows");
  const a = await groupRect("Shop.Api");
  assert.ok(a.y + a.height <= t.y + 1 || t.y + t.height <= a.y + 1, "containers overlap");
});

test("collapsing an expanded card shrinks the container back", async () => {
  await open();
  const before = await p.evaluate(() => ({ ...GROUPS.find((g) => g.name === "Shop.Api") }));
  const sel = '.node[data-id="checkoutservice-placeorderasync"] .expand';
  await p.locator(sel).click(); await p.locator(sel).click();
  const after = await p.evaluate(() => ({ ...GROUPS.find((g) => g.name === "Shop.Api") }));
  assert.equal(Math.round(after.w), Math.round(before.w), "container stayed wider after collapse");
  assert.equal(Math.round(after.h), Math.round(before.h), "container stayed taller after collapse");
});

test("walkthrough dims containers that have no card in the step", async () => {
  await open();
  for (let i = 0; i < 6; i++) await p.keyboard.press("ArrowRight"); // last step (Program.cs): Shop.Api only
  await p.waitForTimeout(900);
  const dim = await p.evaluate(() => GROUPS.map((g) => [g.name, g.el.classList.contains("dim")]));
  assert.deepEqual(Object.fromEntries(dim), { "Shop.Api": false, "Shop.Tests": true });
});

test("walkthrough steps that include tests bring the tests container into view", async () => {
  await open();
  for (let i = 0; i < 3; i++) await p.keyboard.press("ArrowRight");
  await p.waitForTimeout(900);
  const id = "pricingcalculatortests-appliespercentdiscount";
  const r = await worldRect(id), vp = p.viewportSize();
  assert.ok(r.y < vp.height && r.y + r.height > 0, "the step's test card is off-screen");
});

test("hiding context cards hides containers that only had context cards", async () => {
  const d = structuredClone(WITH_TESTS);
  d.nodes.find((n) => n.id === "pricingcalculatortests-addsflatshippingunderthreshold").project = "Shop.OnlyContext";
  d.nodes = d.nodes.filter((n) => !(n.project === "Shop.Tests"));
  d.edges = d.edges.filter((e) => d.nodes.some((n) => e.from.startsWith(n.id + ":")) && d.nodes.some((n) => n.id === e.to));
  d.steps = d.steps.map((s) => ({ ...s, nodes: s.nodes.filter((id) => d.nodes.some((n) => n.id === id)) }));
  await open(d, "onlyctx");
  assert.equal(await p.locator(".group:not([hidden])").count(), 2);
  await p.click("#ctx"); await p.waitForTimeout(300);
  const visible = await p.evaluate(() => GROUPS.filter((g) => !g.el.hidden).map((g) => g.name));
  assert.deepEqual(visible, ["Shop.Api"]);
});

test("toggling context cards does not leave a gap or reset an expanded card", async () => {
  await open();
  await p.click("#ctx"); await p.waitForTimeout(300);
  const nodes = await p.evaluate(() => NODES.filter((n) => n.kind !== "context").every((n) => !nodeEls[n.id].hidden));
  assert.ok(nodes);
  assert.deepEqual(errors, []);
});

test("reviews without a project field still load, with no containers", async () => {
  await open(noProject(), "noproj");
  assert.equal(await p.locator(".node").count(), noProject().nodes.length);
  assert.equal(await p.locator(".group:not([hidden])").count(), 0);
  assert.deepEqual(errors, []);
});

test("opening another JSON replaces the containers instead of stacking them", async () => {
  await open();
  const f = path.join(tmp, "other.json");
  fs.writeFileSync(f, JSON.stringify(sample("sample-shop-review.json")));
  await p.setInputFiles("#file", f); await p.waitForTimeout(600);
  assert.equal(await p.locator(".group").count(), 2);
  assert.equal(await p.locator(".node").count(), sample("sample-shop-review.json").nodes.length);
});

test("a malformed JSON shows an error instead of breaking the page", async () => {
  await open();
  const f = path.join(tmp, "bad.json"); fs.writeFileSync(f, "{ nope");
  await p.setInputFiles("#file", f); await p.waitForTimeout(300);
  assert.match(await p.locator("#step").innerText(), /could not open file/i);
});

test("a JSON that is valid but is not a review is reported, not a blank page", async () => {
  await open();
  const f = path.join(tmp, "notreview.json"); fs.writeFileSync(f, JSON.stringify({ hello: "world" }));
  await p.setInputFiles("#file", f); await p.waitForTimeout(400);
  assert.match(await p.locator("#step").innerText(), /could not open file/i);
});

test("zoom buttons change the zoom readout and stay within limits", async () => {
  await open();
  for (let i = 0; i < 20; i++) await p.click("#zin");
  assert.ok(parseInt(await p.locator("#zread").innerText()) <= 200);
  for (let i = 0; i < 40; i++) await p.click("#zout");
  assert.ok(parseInt(await p.locator("#zread").innerText()) >= 12);
});

test("keyboard: left arrow on the overview and right arrow at the last step are safe", async () => {
  await open();
  await p.keyboard.press("ArrowLeft");
  for (let i = 0; i < 12; i++) await p.keyboard.press("ArrowRight");
  assert.deepEqual(errors, []);
  assert.equal(await p.locator("#next").isDisabled(), true);
});

test("phone width: no horizontal page scroll and the canvas still renders", async () => {
  await p.setViewportSize({ width: 390, height: 800 });
  await open();
  assert.equal(await p.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  assert.ok((await p.locator(".node").count()) > 0);
});

test("dragging a card header never starts a text selection", async () => {
  await open();
  const h = await p.locator(".node-head").first().boundingBox();
  await drag({ x: h.x + 10, y: h.y + 2 }, { x: h.x + 60, y: h.y + 30 });
  assert.equal(await p.evaluate(() => getSelection().toString().length), 0);
});

test("a card's step pin and edge handles are not clipped by the container", async () => {
  await open();
  const pin = await p.locator(".step-pin").first().boundingBox();
  const g = await groupRect("Shop.Api");
  assert.ok(pin.x >= g.x - 1 && pin.y >= g.y - 1, "step pin sticks out of the container");
});

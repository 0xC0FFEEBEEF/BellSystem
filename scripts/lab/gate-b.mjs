// Gate B: the real office page captures "microphone" audio (Chrome fake device fed with
// tests/audio/gateb-mic-input.wav), streams it live through the server to the Windows endpoint.
export default async (page) => {
  await page.goto("/announce");
  await page.waitFor(`document.querySelector("button.talk") && !document.querySelector("button.talk").disabled`);
  // latch mode so one click starts and one click stops
  await page.eval(`(() => { const l = [...document.querySelectorAll("label.check")].find(x => x.textContent.includes("Latch")); const c = l.querySelector("input"); if (!c.checked) c.click(); })()`);
  await page.sleep(300);
  const clickAt = Date.now();
  await page.click("button.talk");
  console.log("talk clicked at", clickAt);
  await page.waitFor(`document.querySelector(".status-line")?.textContent.includes("Ready")`, 20000);
  const readyAt = Date.now();
  console.log("ready after", readyAt - clickAt, "ms;", await page.eval(`document.querySelector(".status-line").textContent`));
  await page.shot("docs/evidence/ui/announce-live.png");
  // the speech (7.64 s) starts 9.0 s into the input file: keep talking until it has finished
  const first = await page.eval("window.__sbFirstMicFrameAt");
  await page.sleep(Math.max(0, first + 17500 - Date.now()));
  await page.click("button.talk");              // finish
  await page.waitFor(`document.querySelector(".status-line")?.textContent.includes("ended")`, 20000);
  const v = await page.eval(`({ first: window.__sbFirstMicFrameAt, firstSent: window.__sbFirstSentAt, seq: window.__sbFirstSentSeq,
    text: document.querySelector(".status-line").textContent, small: [...document.querySelectorAll("p.dim.small")].map(p => p.textContent).join(" | "),
    counts: document.querySelector(".counts")?.textContent })`);
  console.log("RESULT " + JSON.stringify({ clickAt, readyAt, ...v }));
  await page.shot("docs/evidence/ui/announce-ended.png");
};

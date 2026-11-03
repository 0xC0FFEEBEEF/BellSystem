// Screenshot every office page (desktop width) plus the dashboard at phone width.
const OUT = process.env.OUT ?? "docs/evidence/ui";
export default async (page) => {
  for (const [p, name] of [["/", "dashboard"], ["/announce", "announce"], ["/bells", "bells-ring"], ["/endpoints", "endpoints"], ["/history", "history"], ["/admin", "admin"]]) {
    await page.goto(p);
    await page.sleep(1500);
    await page.shot(`${OUT}/${name}.png`);
    const text = await page.eval("document.querySelector('main')?.innerText.slice(0, 400)");
    console.log(`== ${p}\n${text}\n`);
  }
  await page.goto("/bells"); await page.click("Schedules"); await page.sleep(800); await page.shot(`${OUT}/bells-schedules.png`);
  await page.click("Calendar & publish"); await page.sleep(1200); await page.shot(`${OUT}/bells-calendar.png`);
  await page.click("Bell sounds"); await page.sleep(800); await page.shot(`${OUT}/bells-sounds.png`);
  await page.goto("/endpoints"); await page.click("table.hover tbody tr"); await page.sleep(1500); await page.shot(`${OUT}/endpoint-detail.png`);
  await page.goto("/admin"); await page.click("Updates"); await page.sleep(1200); await page.shot(`${OUT}/admin-updates.png`);
};

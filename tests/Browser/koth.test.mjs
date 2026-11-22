import assert from "node:assert/strict";
import { createHash } from "node:crypto";

// Invoked by Quasar.Wasm.DashboardChecks inside its disposable installation.
export async function checkPlugin(page, base) {
    const plugin = "owendb.koth";
    const artifact = page.waitForResponse(response =>
        response.url().startsWith(base + "_quasar/sdk/v1/ui/assemblies/" + plugin + "/"));
    const stylesheet = page.waitForResponse(response =>
        response.url() === base + "_quasar/plugins/" + plugin + "/koth.css");
    await page.goto(base + "koth");
    const assembly = await artifact;
    assert.equal(assembly.status(), 200);
    const digest = createHash("sha256").update(await assembly.body()).digest("hex");
    assert.ok(assembly.url().endsWith("/" + digest));
    assert.equal((await stylesheet).status(), 200);

    await page.getByRole("heading", { name: "King of the Hill", exact: true }).waitFor();
    const server = page.locator(".koth-server-panel").first();
    await server.locator("summary").click();
    // The host fixture advertises a server without the KOTH mod/companion.
    await server.getByRole("alert").filter({ hasText: "Could not load KOTH scores" }).waitFor();

    const channel = page.getByLabel("KOTH channel ID");
    await channel.fill("123456789012345678");
    await page.getByRole("button", { name: "Save Discord settings" }).click();
    await page.getByText("KOTH Discord settings saved.").waitFor();
    await page.reload();
    await page.waitForFunction(() => document.querySelector("#koth-channel")?.value === "123456789012345678");
}

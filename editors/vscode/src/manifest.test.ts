// Checks the extension's manifest keeps the meep command out of an untrusted folder's hands (S8).
import { readFileSync } from "fs";
import { join } from "path";

const manifest = JSON.parse(readFileSync(join(__dirname, "..", "package.json"), "utf8"));
const failures: string[] = [];

const meepPath = manifest.contributes?.configuration?.properties?.["meeplang.meepPath"];
if (meepPath?.scope !== "machine-overridable")
  failures.push(`meeplang.meepPath's scope is ${meepPath?.scope}, not machine-overridable.`);

const untrusted = manifest.capabilities?.untrustedWorkspaces;
if (untrusted?.supported !== "limited")
  failures.push(`untrustedWorkspaces.supported is ${untrusted?.supported}, not limited.`);
if (!untrusted?.restrictedConfigurations?.includes("meeplang.meepPath"))
  failures.push("meeplang.meepPath isn't restricted in folders you haven't trusted.");

if (failures.length > 0) {
  console.error(failures.join("\n"));
  process.exit(1);
}
console.log("The manifest keeps meeplang.meepPath to your own settings in folders you haven't trusted.");

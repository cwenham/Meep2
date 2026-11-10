// Runs inside VS Code with the extension loaded: opens a pipeline and checks that meep lsp reports its mistake,
// clears it once it's fixed, offers verbs as you type and describes them on hover.
import * as assert from "assert";
import * as vscode from "vscode";

async function until<T>(what: string, find: () => T | undefined, seconds = 30): Promise<T> {
  const deadline = Date.now() + seconds * 1000;
  for (;;) {
    const found = find();
    if (found !== undefined) return found;
    if (Date.now() > deadline) throw new Error(`Gave up waiting for ${what} after ${seconds} seconds.`);
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
}

export async function run(): Promise<void> {
  const folder = vscode.workspace.workspaceFolders?.[0]?.uri;
  assert.ok(folder, "VS Code opened no folder.");
  const uri = vscode.Uri.joinPath(folder, "ticker.meep");
  await vscode.workspace.fs.writeFile(uri, new TextEncoder().encode('Interval 1s\n    WriteLin "{msg.Count}"\n'));
  const document = await vscode.workspace.openTextDocument(uri);
  const editor = await vscode.window.showTextDocument(document);
  assert.strictEqual(document.languageId, "meeplang", ".meep files aren't Meeplang.");

  // The mistake meep run would find, where it is.
  const [mistake] = await until("meep lsp to report the misspelt verb", () => {
    const found = vscode.languages.getDiagnostics(uri);
    return found.length > 0 ? found : undefined;
  });
  assert.strictEqual(mistake.message, "There's no module called WriteLin. Did you mean 'WriteLine'?");
  assert.strictEqual(mistake.severity, vscode.DiagnosticSeverity.Error);
  assert.deepStrictEqual([mistake.range.start.line, mistake.range.start.character], [1, 4]);

  // Fixing it clears it.
  await editor.edit((edit) => edit.insert(new vscode.Position(1, 12), "e"));
  await until("the mistake to clear once it's fixed", () => (vscode.languages.getDiagnostics(uri).length === 0 ? true : undefined));

  // Verbs are offered as you type one.
  await editor.edit((edit) => edit.insert(new vscode.Position(2, 0), "    Wri"));
  const completions = await vscode.commands.executeCommand<vscode.CompletionList>(
    "vscode.executeCompletionItemProvider", uri, new vscode.Position(2, 7));
  const labels = completions.items.map((item) => (typeof item.label === "string" ? item.label : item.label.label));
  assert.ok(labels.includes("WriteLine"), `WriteLine isn't offered; these are: ${labels.slice(0, 20).join(", ")}`);

  // Hovering over a verb describes it.
  const hovers = await vscode.commands.executeCommand<vscode.Hover[]>("vscode.executeHoverProvider", uri, new vscode.Position(0, 3));
  const hover = hovers.flatMap((h) => h.contents).map((c) => (typeof c === "string" ? c : c.value)).join("\n");
  assert.ok(hover.includes("**Interval**"), `Hovering over Interval shows: ${hover || "nothing"}`);

  await vscode.commands.executeCommand("workbench.action.files.revert");
  console.log("In VS Code, meep lsp reported the mistake, cleared it once fixed, offered WriteLine and described Interval.");
}

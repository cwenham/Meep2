// Runs inside VS Code with the extension loaded: opens a pipeline and checks that meep lsp reports its mistake,
// clears it once it's fixed, offers verbs as you type, describes them on hover and colours the file from Meep's parser.
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

  // Semantic tokens colour the verb, the duration and the member from Meep's own parser.
  const legend = await vscode.commands.executeCommand<vscode.SemanticTokensLegend>("vscode.provideDocumentSemanticTokensLegend", uri);
  assert.ok(legend, "meep lsp gave no semantic tokens legend.");
  let tokens: vscode.SemanticTokens | undefined;
  for (let tries = 0; tries < 50 && !(tokens && tokens.data.length > 0); tries++) {
    if (tries > 0) await new Promise((resolve) => setTimeout(resolve, 100));
    tokens = await vscode.commands.executeCommand<vscode.SemanticTokens>("vscode.provideDocumentSemanticTokens", uri);
  }
  assert.ok(tokens && tokens.data.length > 0, "meep lsp gave no semantic tokens.");
  const coloured: string[] = [];
  let line = 0;
  let character = 0;
  for (let i = 0; i < tokens.data.length; i += 5) {
    line += tokens.data[i];
    character = tokens.data[i] === 0 ? character + tokens.data[i + 1] : tokens.data[i + 1];
    const text = document.lineAt(line).text.substr(character, tokens.data[i + 2]);
    coloured.push(`${text}:${legend.tokenTypes[tokens.data[i + 3]]}`);
  }
  for (const expected of ["Interval:function", "1s:number", "WriteLine:function", "msg:variable", "Count:property"]) {
    assert.ok(coloured.includes(expected), `${expected} isn't among the semantic tokens: ${coloured.join(" ")}`);
  }

  await vscode.commands.executeCommand("workbench.action.files.revert");
  console.log("In VS Code, meep lsp reported the mistake, cleared it once fixed, offered WriteLine, described Interval and coloured the file.");
}

import * as vscode from "vscode";
import { LanguageClient, LanguageClientOptions, ServerOptions } from "vscode-languageclient/node";

let client: LanguageClient | undefined;

// Starts meep lsp for .meep files, so the editor shows the mistakes meep run would find, as you type.
export async function activate(_context: vscode.ExtensionContext): Promise<void> {
  const meep = vscode.workspace.getConfiguration("meeplang").get<string>("meepPath") || "meep";
  const server: ServerOptions = { command: meep, args: ["lsp"] };
  const options: LanguageClientOptions = {
    documentSelector: [{ scheme: "file", language: "meeplang" }],
  };
  client = new LanguageClient("meeplang", "Meeplang", server, options);
  try {
    await client.start();
  } catch (error) {
    void vscode.window.showWarningMessage(
      `Meeplang couldn't start "${meep} lsp": ${error}. Set meeplang.meepPath to the meep command.`);
  }
}

// Stops the server, if it started: a client that never started can't be stopped.
export async function deactivate(): Promise<void> {
  if (client?.isRunning()) await client.stop();
}

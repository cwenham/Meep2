# Meeplang for VS Code

Colouring for Meeplang, Meep's pipeline language, and the mistakes `meep run` would find, shown as you type.

The mistakes come from `meep lsp`, the language server built into the meep command, which checks a file with
Meeplang's own parser and loader. It also offers verbs, modifiers, message types and settings as you type, and shows a
verb's inflections and parameters when you hover over it.

## Setting it up

1. Put `meep` on your PATH, or set `meeplang.meepPath` to it.
2. Build and install the extension:

   ```
   npm install
   npm run compile
   npx @vscode/vsce package
   code --install-extension meeplang-0.1.0.vsix
   ```

Plugins' verbs are checked too, using the plugins already in Meep's cache: nothing is downloaded while you edit, so a
plugin is reported on its `#plugin` line until the pipeline has run once to install it. Only plugins `meep run` would
trust without `--dev` are loaded, so opening a file can't load code you haven't trusted.

## Testing it

- `npm test` colours a sample pipeline with VS Code's own TextMate engine and checks each part gets the colour it
  should.
- `npm run test:editor` runs the extension in the VS Code installed on this machine, with a profile of its own, and
  checks that a mistake is shown where it is, cleared once fixed, and that verbs are offered and described. It opens
  a window while it runs. It tests the meep built in `src/Meep2.Cli`, or `MEEP_PATH`; set `VSCODE_PATH` if VS Code
  isn't in the usual place.

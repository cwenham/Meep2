// Colours a sample with VS Code's own TextMate engine and checks each part gets the scope it should.
import { readFileSync } from "fs";
import { join } from "path";
import * as oniguruma from "vscode-oniguruma";
import { INITIAL, IGrammar, Registry, parseRawGrammar } from "vscode-textmate";

const sample = [
  /*  0 */ `#include "shared/transit.meep"   // the timetable`,
  /*  1 */ `#plugin github.com/you/MeepTransit 1.2.0 sha256:${"A".repeat(64)}`,
  /*  2 */ `#plugin '../Transit/bin/Debug/net11.0/Transit.dll'`,
  /*  3 */ `const Api = "https://api.busline.example/v1"`,
  /*  4 */ `defaults`,
  /*  5 */ `    timeout = 5s`,
  /*  6 */ `template Departures(Stop, Limit = 10)`,
  /*  7 */ `    Fetch "{Api}/schedule/{Stop}"`,
  /*  8 */ `        Where msg.minutesAway < Limit and not msg.cancelled`,
  /*  9 */ `Interval 1m @no-errors`,
  /* 10 */ `    Departures "12" limit=30 as Board`,
  /* 11 */ `        WriteLine "{msg.route} at {msg.due:HH:mm}, {{not this}}" // said aloud`,
  /* 12 */ `+ Listen "http://0.0.0.0:8080/orders"`,
  /* 13 */ `    + Fetch.HTTP 'it''s {raw}'`,
  /* 14 */ `        Where msg is not Reading or fam.Board.Count >= 2`,
  /* 15 */ `            Transit.Departures.Live`,
  /* 16 */ `    Distribute 4 by msg.CustomerId as Spread`,
];

// [line, text on that line, the scope its tokens should have, or null for none but the file's own].
const expected: [number, string, string | null][] = [
  [0, "#include", "keyword.control.directive.meeplang"],
  [0, `"shared/transit.meep"`, "string.quoted.double.meeplang"],
  [0, "// the timetable", "comment.line.double-slash.meeplang"],
  [1, "#plugin", "keyword.control.directive.meeplang"],
  [1, `sha256:${"A".repeat(64)}`, "constant.other.sha.meeplang"],
  [2, `'../Transit/bin/Debug/net11.0/Transit.dll'`, "string.quoted.single.meeplang"],
  [3, "const", "keyword.other.const.meeplang"],
  [3, "Api", "variable.other.constant.meeplang"],
  [3, `"https://api.busline.example/v1"`, "string.quoted.double.meeplang"],
  [4, "defaults", "keyword.other.defaults.meeplang"],
  [5, "timeout", "variable.parameter.meeplang"],
  [5, "5s", "constant.numeric.duration.meeplang"],
  [6, "template", "keyword.other.template.meeplang"],
  [6, "Departures", "entity.name.function.template.meeplang"],
  [6, "Limit", "variable.parameter.meeplang"],
  [6, "10", "constant.numeric.meeplang"],
  [7, "Fetch", "entity.name.type.module.meeplang"],
  [7, "{", "punctuation.section.template.begin.meeplang"],
  [7, "Api", "meta.template.expression.meeplang"],
  [7, "/schedule/", "string.quoted.double.meeplang"],
  [8, "Where", "entity.name.type.module.meeplang"],
  [8, "msg", "variable.language.meeplang"],
  [8, "minutesAway", null],
  [8, "and", "keyword.operator.logical.meeplang"],
  [8, "not", "keyword.operator.logical.meeplang"],
  [9, "Interval", "entity.name.type.module.meeplang"],
  [9, "1m", "constant.numeric.duration.meeplang"],
  [9, "@no-errors", "storage.modifier.meeplang"],
  [10, "Departures", "entity.name.type.module.meeplang"],
  [10, "limit", "variable.parameter.meeplang"],
  [10, "as", "keyword.other.as.meeplang"],
  [10, "Board", "variable.other.name.meeplang"],
  [11, "WriteLine", "entity.name.type.module.meeplang"],
  [11, "msg", "variable.language.meeplang"],
  [11, "HH:mm", "constant.other.format.meeplang"],
  [11, "{{", "constant.character.escape.meeplang"],
  [11, "not this", "string.quoted.double.meeplang"],
  [11, "// said aloud", "comment.line.double-slash.meeplang"],
  [12, "+", "keyword.operator.feeder.meeplang"],
  [12, "Listen", "entity.name.type.module.meeplang"],
  [13, "+", "keyword.operator.feeder.meeplang"],
  [13, "Fetch.HTTP", "entity.name.type.module.meeplang"],
  [13, "''", "constant.character.escape.meeplang"],
  [13, "{raw}", "string.quoted.single.meeplang"],
  [14, "Where", "entity.name.type.module.meeplang"],
  [14, "is not", "keyword.operator.is.meeplang"],
  [14, "Reading", "entity.name.type.message.meeplang"],
  [14, "or", "keyword.operator.logical.meeplang"],
  [14, "fam", "variable.language.meeplang"],
  [14, ">=", "keyword.operator.meeplang"],
  [15, "Transit.Departures.Live", "entity.name.type.module.meeplang"],
  [16, "Distribute", "entity.name.type.module.meeplang"],
  [16, "by", "keyword.other.by.meeplang"],
  [16, "msg", "variable.language.meeplang"],
  [16, "as", "keyword.other.as.meeplang"],
];

async function loadGrammar(): Promise<IGrammar> {
  const wasm = readFileSync(require.resolve("vscode-oniguruma/release/onig.wasm"));
  await oniguruma.loadWASM(wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength));
  const path = join(__dirname, "..", "syntaxes", "meeplang.tmLanguage.json");
  const registry = new Registry({
    onigLib: Promise.resolve({
      createOnigScanner: (patterns: string[]) => new oniguruma.OnigScanner(patterns),
      createOnigString: (text: string) => new oniguruma.OnigString(text),
    }),
    loadGrammar: async (scope: string) => (scope === "source.meeplang" ? parseRawGrammar(readFileSync(path, "utf8"), path) : null),
  });
  const grammar = await registry.loadGrammar("source.meeplang");
  if (!grammar) throw new Error("The grammar didn't load.");
  return grammar;
}

async function main(): Promise<void> {
  const grammar = await loadGrammar();
  const lines: { start: number; end: number; scopes: string[] }[][] = [];
  let state = INITIAL;
  for (const line of sample) {
    const result = grammar.tokenizeLine(line, state);
    lines.push(result.tokens.map((t) => ({ start: t.startIndex, end: t.endIndex, scopes: t.scopes })));
    state = result.ruleStack;
  }

  const failures: string[] = [];
  for (const [line, text, scope] of expected) {
    const at = sample[line].indexOf(text);
    if (at < 0) {
      failures.push(`line ${line}: the sample has no "${text}"`);
      continue;
    }
    for (const token of lines[line].filter((t) => t.start < at + text.length && t.end > at)) {
      const scopes = token.scopes.slice(1);
      const ok = scope === null ? scopes.length === 0 : scopes.includes(scope);
      if (!ok) {
        const shown = sample[line].slice(token.start, token.end);
        failures.push(`line ${line}, "${shown}" in "${text}": has ${scopes.join(" ") || "no scope"}, expected ${scope ?? "none"}`);
      }
    }
  }

  if (failures.length > 0) {
    console.error(failures.join("\n"));
    process.exit(1);
  }
  console.log(`The grammar colours all ${expected.length} parts of the sample as it should.`);
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});

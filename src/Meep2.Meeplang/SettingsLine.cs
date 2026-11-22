using Meep2.Meeplang.Syntax;

namespace Meep2.Meeplang;

/// <summary>A value in a settings file, worked out, with where it was written.</summary>
/// <param name="Value">The value: text, a number, a duration, true or false.</param>
/// <param name="Span">Where it's written.</param>
public sealed record SettingValue(object? Value, SourceSpan Span);

/// <summary>One line of a settings file, written as a module line is: a word, a value, and named values.</summary>
/// <param name="Word">The word it starts with, such as <c>Start</c>.</param>
/// <param name="Value">The value written after the word, if any.</param>
/// <param name="Named">The values given by name, such as <c>watch=true</c>.</param>
/// <param name="Span">Where the word is.</param>
public sealed record SettingsLine(string Word, SettingValue? Value, IReadOnlyDictionary<string, SettingValue> Named, SourceSpan Span);

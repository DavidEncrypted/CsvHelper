// Copyright 2009-2024 Josh Close
// This file is a part of CsvHelper and is dual licensed under MS-PL and Apache 2.0.
// See LICENSE.txt for details or visit http://www.opensource.org/licenses/ms-pl.html for MS-PL and http://opensource.org/licenses/Apache-2.0 for Apache 2.0.
// https://github.com/JoshClose/CsvHelper
using System.Globalization;
using System.Text;
using CsvHelper.Configuration;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Xunit;

namespace CsvHelper.Tests.Parsing;

/// <summary>
/// Property-based tests for the two buffers in CsvParser. The property is the
/// same for both: the size of an internal buffer must not change what the
/// parser returns. It may change how often the buffer is refilled or grown,
/// and that is precisely the code these tests exercise.
///
/// Every case is parsed twice, once with the buffer small enough to force
/// growth or refills and once with it large enough that neither happens, and
/// the two must agree. Where the input is well formed, both must also equal
/// the fields the text was built from.
///
/// Both properties were written against mutants that survived Stryker:
///
/// - ProcessFieldBufferSize: the growth guard in ProcessRFC4180Field,
///   ProcessRFC4180BadField and ProcessEscapeField survived every mutation
///   because no test drove an escaped field past the 1024 default. Fields
///   here are generated across that boundary, and the small leg starts the
///   buffer at 8 so growth happens on every case. All three mutants now die.
///
/// - BufferSize: the refill check after bufferPosition++ inside ReadDelimiter
///   and ReadNewLine survived. This property parses every case at BufferSize
///   1..40, so a two-character delimiter and newline are split across a refill
///   constantly - and the mutants still survive. They are equivalent: the
///   check at the top of the same loop already returns Incomplete on an empty
///   buffer, so the second check only decides whether the loop spins once
///   more. The property stays as the regression guard for the refill path.
/// </summary>
public class BufferInvarianceProperties
{
	private const string Delimiter = "||";
	private const string NewLine = "\r\n";

	/// <summary>
	/// Large enough that the parse never refills or grows: no generated case
	/// comes close, so this leg is the oracle for "what the parser means".
	/// </summary>
	private const int Unbounded = 1 << 20;

	private static readonly int DefaultProcessFieldBufferSize = new CsvConfiguration(CultureInfo.InvariantCulture).ProcessFieldBufferSize;

	[Property(Arbitrary = new[] { typeof(CsvArbitraries) }, MaxTest = 200)]
	public void ProcessFieldBufferSize_DoesNotChangeOutput(CsvCase c)
	{
		// 8 is far below every generated field that needs processing, so the
		// doubling loop in ProcessRFC4180Field / ProcessRFC4180BadField /
		// ProcessEscapeField runs, repeatedly, for every case.
		var small = Parse(c, bufferSize: Unbounded, processFieldBufferSize: 8);
		var large = Parse(c, bufferSize: Unbounded, processFieldBufferSize: Unbounded);

		AssertSameRows(large, small);
		if (c.Expected != null)
		{
			AssertSameRows(c.Expected, small);
		}
	}

	[Property(Arbitrary = new[] { typeof(CsvArbitraries) }, MaxTest = 200)]
	public void BufferSize_DoesNotChangeOutput(CsvCase c)
	{
		// BufferSize is the read buffer. At 1..40 chars a two-character
		// delimiter or newline is split across a refill many times per case.
		// The processing buffer is left at its default so this leg also crosses
		// the 1024 boundary the way a real caller would.
		var small = Parse(c, bufferSize: c.BufferSize, processFieldBufferSize: DefaultProcessFieldBufferSize);
		var large = Parse(c, bufferSize: Unbounded, processFieldBufferSize: Unbounded);

		AssertSameRows(large, small);
		if (c.Expected != null)
		{
			AssertSameRows(c.Expected, small);
		}
	}

	private static string[][] Parse(CsvCase c, int bufferSize, int processFieldBufferSize)
	{
		var config = new CsvConfiguration(CultureInfo.InvariantCulture)
		{
			Delimiter = Delimiter,
			Mode = c.Mode,
			Escape = c.Mode == CsvMode.Escape ? '\\' : '"',
			BufferSize = bufferSize,
			ProcessFieldBufferSize = processFieldBufferSize,
			BadDataFound = null,
			// A single empty field in Escape mode encodes as an empty line.
			// Keep it a row rather than letting the parser skip it.
			IgnoreBlankLines = false,
		};
		if (c.SetNewLine)
		{
			config.NewLine = NewLine;
		}

		var rows = new List<string[]>();
		using var reader = new StringReader(c.Text);
		using var parser = new CsvParser(reader, config);
		while (parser.Read())
		{
			rows.Add(parser.Record!);
		}

		return rows.ToArray();
	}

	private static void AssertSameRows(string[][] expected, string[][] actual)
	{
		Assert.Equal(expected.Length, actual.Length);
		for (var r = 0; r < expected.Length; r++)
		{
			Assert.Equal(expected[r].Length, actual[r].Length);
			for (var f = 0; f < expected[r].Length; f++)
			{
				if (expected[r][f] != actual[r][f])
				{
					Assert.True(false, $"Row {r} field {f} differs (expected length {expected[r][f].Length}, actual length {actual[r][f].Length}).");
				}
			}
		}
	}

	/// <summary>
	/// One generated input: the CSV text, how it should be parsed, and the
	/// fields it was built from when the encoding is lossless.
	/// </summary>
	public sealed class CsvCase
	{
		public CsvCase(string[][] fields, CsvMode mode, bool badData, bool setNewLine, int bufferSize)
		{
			Fields = fields;
			Mode = mode;
			BadData = badData;
			SetNewLine = setNewLine;
			BufferSize = bufferSize;
			Text = Encode(fields, mode, badData);
			// Bad data has no single right answer, so it is checked only for
			// agreement between the two parses.
			Expected = badData ? null : fields;
		}

		public string[][] Fields { get; }
		public CsvMode Mode { get; }
		public bool BadData { get; }
		public bool SetNewLine { get; }
		public int BufferSize { get; }
		public string Text { get; }
		public string[][]? Expected { get; }

		public override string ToString()
		{
			var lengths = string.Join(" ", Fields.Select(r => "[" + string.Join(",", r.Select(f => f.Length)) + "]"));
			var text = Text.Length <= 200 ? " text: " + Text.Replace("\r", "\\r").Replace("\n", "\\n") : "";
			return $"{Mode}{(BadData ? "+BadData" : "")} NewLine={(SetNewLine ? "set" : "auto")} BufferSize={BufferSize} field lengths {lengths}{text}";
		}

		private static string Encode(string[][] rows, CsvMode mode, bool badData)
		{
			var sb = new StringBuilder();
			foreach (var row in rows)
			{
				for (var i = 0; i < row.Length; i++)
				{
					if (i > 0)
					{
						sb.Append(Delimiter);
					}

					if (mode == CsvMode.Escape)
					{
						AppendEscaped(sb, row[i]);
					}
					else
					{
						AppendRfc4180(sb, row[i], badData);
					}
				}

				sb.Append(NewLine);
			}

			return sb.ToString();
		}

		private static void AppendRfc4180(StringBuilder sb, string field, bool badData)
		{
			// The delimiter is two characters, so a field that merely ends with a
			// prefix of it ("bc|" followed by "||") would be read greedily as the
			// delimiter plus a stray "|". Quote whenever the delimiter would be
			// found anywhere before the field's own end.
			var needsQuotes =
				field.Length == 0 ||
				field.IndexOf('"') >= 0 ||
				field.IndexOf('\r') >= 0 ||
				field.IndexOf('\n') >= 0 ||
				(field + Delimiter).IndexOf(Delimiter, StringComparison.Ordinal) < field.Length;

			if (!needsQuotes && !badData)
			{
				sb.Append(field);
				return;
			}

			sb.Append('"').Append(field.Replace("\"", "\"\"")).Append('"');
			if (badData)
			{
				// Text after the closing quote makes the field bad data and
				// routes it through ProcessRFC4180BadField.
				sb.Append("x");
			}
		}

		private static void AppendEscaped(StringBuilder sb, string field)
		{
			foreach (var ch in field)
			{
				if (ch == '\\' || ch == '|' || ch == '\r' || ch == '\n')
				{
					sb.Append('\\');
				}

				sb.Append(ch);
			}
		}
	}

	public static class CsvArbitraries
	{
		// Quotes, the delimiter character, and both line-ending characters are
		// weighted in so that most fields need escaping and therefore go
		// through the processing buffer.
		private static readonly Gen<char> FieldChar = Gen.Frequency(
			(6, Gen.Elements('a', 'b', 'c', ' ')),
			(2, Gen.Constant('"')),
			(2, Gen.Constant('|')),
			(1, Gen.Constant('\\')),
			(1, Gen.Constant('\r')),
			(1, Gen.Constant('\n')));

		// Lengths cluster around the 1024 default of ProcessFieldBufferSize and
		// well past it, with a tail of short fields so rows are not all huge.
		private static readonly Gen<int> FieldLength = Gen.Frequency(
			(3, Gen.Choose(0, 40)),
			(3, Gen.Choose(1000, 1100)),
			(2, Gen.Choose(1100, 5000)));

		private static readonly Gen<string> Field =
			FieldLength.SelectMany(n => Gen.ArrayOf(FieldChar, n)).Select(chars => new string(chars));

		private static readonly Gen<string[]> Row =
			Gen.Choose(1, 3).SelectMany(n => Gen.ArrayOf(Field, n));

		private static readonly Gen<string[][]> Table =
			Gen.Choose(1, 3).SelectMany(n => Gen.ArrayOf(Row, n));

		private static readonly Gen<(CsvMode mode, bool badData)> Shape = Gen.Frequency(
			(2, Gen.Constant((CsvMode.RFC4180, false))),
			(1, Gen.Constant((CsvMode.RFC4180, true))),
			(1, Gen.Constant((CsvMode.Escape, false))));

		public static Arbitrary<CsvCase> CsvCase()
		{
			var gen =
				from table in Table
				from shape in Shape
				from setNewLine in Gen.Elements(true, false)
				from bufferSize in Gen.Choose(1, 40)
				select new CsvCase(table, shape.mode, shape.badData, setNewLine, bufferSize);

			return Arb.From(gen, Shrink);
		}

		/// <summary>
		/// Drop a row, drop a field, or halve a field. Enough that a failure
		/// shrinks to roughly the smallest input that still fails.
		/// </summary>
		private static IEnumerable<CsvCase> Shrink(CsvCase c)
		{
			var rows = c.Fields;

			for (var r = 0; r < rows.Length; r++)
			{
				if (rows.Length > 1)
				{
					yield return With(c, rows.Where((_, i) => i != r).ToArray());
				}

				for (var f = 0; f < rows[r].Length; f++)
				{
					if (rows[r].Length > 1)
					{
						yield return With(c, Replace(rows, r, rows[r].Where((_, i) => i != f).ToArray()));
					}

					var field = rows[r][f];
					if (field.Length > 0)
					{
						yield return With(c, Replace(rows, r, Replace(rows[r], f, field.Substring(0, field.Length / 2))));
						yield return With(c, Replace(rows, r, Replace(rows[r], f, field.Substring(0, field.Length - 1))));
					}
				}
			}
		}

		private static CsvCase With(CsvCase c, string[][] rows) =>
			new(rows, c.Mode, c.BadData, c.SetNewLine, c.BufferSize);

		private static T[] Replace<T>(T[] array, int index, T value)
		{
			var copy = (T[])array.Clone();
			copy[index] = value;
			return copy;
		}
	}
}

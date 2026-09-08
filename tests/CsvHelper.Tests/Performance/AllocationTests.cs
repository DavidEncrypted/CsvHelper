// Copyright 2009-2024 Josh Close
// This file is a part of CsvHelper and is dual licensed under MS-PL and Apache 2.0.
// See LICENSE.txt for details or visit http://www.opensource.org/licenses/ms-pl.html for MS-PL and http://opensource.org/licenses/Apache-2.0 for Apache 2.0.
// https://github.com/JoshClose/CsvHelper
#if NET6_0_OR_GREATER
using System.Globalization;
using System.Text;
using Xunit;

namespace CsvHelper.Tests.Performance;

/// <summary>
/// Guards the allocation invariants documented in
/// .claude/skills/hot-path-constraints/SKILL.md.
///
/// These assert the *marginal* cost of one row, not the total cost of a read.
/// Every measurement reads N rows and 2N rows and subtracts, which cancels all
/// fixed costs - the reader, its buffers, and the record delegate that
/// RecordCreator compiles once per CsvReader instance. What is left is what one
/// extra row costs, which is the thing the invariants actually constrain.
///
/// GC.GetAllocatedBytesForCurrentThread is per thread, so xunit running other
/// tests in parallel cannot perturb these numbers.
///
/// Only compiled for NET6_0_OR_GREATER: GetAllocatedBytesForCurrentThread does
/// not exist on the net462/net47/net48 legs.
/// </summary>
public class AllocationTests
{
	private const int RowCount = 10_000;

	/// <summary>
	/// Maps three columns. Used against both a 3 column and a 50 column file so
	/// the two can be compared directly.
	/// </summary>
	private class ThreeColumns
	{
		public string? Column0 { get; set; }

		public string? Column1 { get; set; }

		public string? Column2 { get; set; }
	}

	[Fact]
	public void GetRecords_AllocatesBoundedBytesPerRow()
	{
		var bytesPerRow = MeasureBytesPerRow(ReadWithGetRecords, columnCount: 3);

		// Measured at 184 bytes per row: one record object plus one string per mapped
		// field. Nothing here scales with the size of the file.
		//
		// This cap is the blunt instrument of the three. It catches a change that
		// allocates something large per row - a buffer, a list, a substring of the
		// row - but it will not notice one extra small string. The two relative
		// tests below are the sensitive ones; this is the safety net under them.
		Assert.True(
			bytesPerRow < 300,
			$"Expected under 300 bytes per row, measured {bytesPerRow:F1}. " +
			"Something in the read path started allocating per row. See hot-path-constraints, " +
			"\"Strings are materialized lazily, once per row\"."
		);
	}

	[Fact]
	public void GetRecords_DoesNotAllocateForUnmappedColumns()
	{
		var narrow = MeasureBytesPerRow(ReadWithGetRecords, columnCount: 3);
		var wide = MeasureBytesPerRow(ReadWithGetRecords, columnCount: 50);

		// Invariant 4: only fields that are actually asked for become strings.
		// Reading 50 columns while mapping 3 must cost about the same as reading 3.
		// If field materialization ever became eager this would be ~17x.
		Assert.True(
			wide < narrow * 2,
			$"Mapping 3 of 50 columns allocated {wide:F1} bytes per row versus {narrow:F1} " +
			"for a 3 column file. Fields are being materialized eagerly rather than on " +
			"demand. See hot-path-constraints, \"Strings are materialized lazily, once per row\"."
		);
	}

	[Fact]
	public void EnumerateRecords_AllocatesLessThanGetRecords()
	{
		var getRecords = MeasureBytesPerRow(ReadWithGetRecords, columnCount: 3);
		var enumerateRecords = MeasureBytesPerRow(ReadWithEnumerateRecords, columnCount: 3);

		// Invariant 12: EnumerateRecords hydrates one instance instead of creating
		// one per row, so it must cost strictly less. It does not reach zero - the
		// field strings are still allocated unless CacheFields is on.
		Assert.True(
			enumerateRecords < getRecords,
			$"EnumerateRecords allocated {enumerateRecords:F1} bytes per row, GetRecords " +
			$"{getRecords:F1}. The record instance is no longer being reused. " +
			"See hot-path-constraints, \"Keep the zero-allocation escape hatch working\"."
		);
	}

	/// <summary>
	/// Reads N and 2N rows and returns the difference divided by N, so every cost
	/// that does not scale with row count drops out.
	/// </summary>
	private static double MeasureBytesPerRow(Func<string, long> read, int columnCount)
	{
		var small = MakeCsv(RowCount, columnCount);
		var large = MakeCsv(RowCount * 2, columnCount);

		// Settle tiered JIT and the one-off expression compilation before measuring.
		read(MakeCsv(100, columnCount));
		read(MakeCsv(100, columnCount));

		var smallBytes = Measure(small, read);
		var largeBytes = Measure(large, read);

		return (largeBytes - smallBytes) / (double)RowCount;
	}

	private static long Measure(string csv, Func<string, long> read)
	{
		var before = GC.GetAllocatedBytesForCurrentThread();
		var rows = read(csv);
		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

		// Touch the result so the read cannot be optimized away.
		Assert.True(rows > 0);

		return allocated;
	}

	private static long ReadWithGetRecords(string csv)
	{
		using var reader = new StringReader(csv);
		using var csvReader = new CsvReader(reader, CultureInfo.InvariantCulture);

		var count = 0L;
		foreach (var record in csvReader.GetRecords<ThreeColumns>())
		{
			count++;
		}

		return count;
	}

	private static long ReadWithEnumerateRecords(string csv)
	{
		using var reader = new StringReader(csv);
		using var csvReader = new CsvReader(reader, CultureInfo.InvariantCulture);

		var record = new ThreeColumns();
		var count = 0L;
		foreach (var _ in csvReader.EnumerateRecords(record))
		{
			count++;
		}

		return count;
	}

	/// <summary>
	/// Builds a deterministic CSV. Every value is 10 characters so string sizes are
	/// stable across runs and across column counts.
	/// </summary>
	private static string MakeCsv(int rowCount, int columnCount)
	{
		var builder = new StringBuilder();

		for (var column = 0; column < columnCount; column++)
		{
			if (column > 0)
			{
				builder.Append(',');
			}

			builder.Append("Column").Append(column);
		}

		builder.Append("\r\n");

		for (var row = 0; row < rowCount; row++)
		{
			for (var column = 0; column < columnCount; column++)
			{
				if (column > 0)
				{
					builder.Append(',');
				}

				builder.Append("value").Append(column.ToString("D5"));
			}

			builder.Append("\r\n");
		}

		return builder.ToString();
	}
}
#endif

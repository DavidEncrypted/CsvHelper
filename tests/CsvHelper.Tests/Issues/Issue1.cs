// Copyright 2009-2024 Josh Close
// This file is a part of CsvHelper and is dual licensed under MS-PL and Apache 2.0.
// See LICENSE.txt for details or visit http://www.opensource.org/licenses/ms-pl.html for MS-PL and http://opensource.org/licenses/Apache-2.0 for Apache 2.0.
// https://github.com/JoshClose/CsvHelper
using CsvHelper.Configuration.Attributes;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Xunit;

namespace CsvHelper.Tests.Issues
{
	/// <summary>
	/// Issue 1: Using FormatAttribute on a record field has no effect during writing.
	/// </summary>
	public class Issue1
	{
		private const string FORMAT = "yyyy-MM-dd";

		[Fact]
		public void WriteRecords_PositionalRecord_FormatAttributeOnParameter_UsesFormat()
		{
			// The reported case: a positional record with [Format] on a constructor parameter.
			var date = new DateTime(2020, 12, 25);
			var records = new List<Person>
			{
				new Person("Alice", date),
			};

			using (var writer = new StringWriter())
			using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
			{
				csv.WriteRecords(records);

				var expected = new StringBuilder();
				expected.Append("Name,Birthday\r\n");
				expected.Append($"Alice,{date.ToString(FORMAT, CultureInfo.InvariantCulture)}\r\n");

				Assert.Equal(expected.ToString(), writer.ToString());
			}
		}

		[Fact]
		public void WriteRecords_ClassWithConstructor_FormatAttributeOnParameter_UsesFormat()
		{
			// A class (not a record) with a parameterised constructor and [Format] on a parameter.
			var date = new DateTime(2020, 12, 25);
			var records = new List<Event>
			{
				new Event(42, date),
			};

			using (var writer = new StringWriter())
			using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
			{
				csv.WriteRecords(records);

				var expected = new StringBuilder();
				expected.Append("Id,OccurredOn\r\n");
				expected.Append($"42,{date.ToString(FORMAT, CultureInfo.InvariantCulture)}\r\n");

				Assert.Equal(expected.ToString(), writer.ToString());
			}
		}

		// Positional record — attributes live on the constructor parameters.
		private record Person(string Name, [Format(FORMAT)] DateTime Birthday);

		private class Event
		{
			public int Id { get; private set; }
			public DateTime OccurredOn { get; private set; }

			public Event(int id, [Format(FORMAT)] DateTime occurredOn)
			{
				Id = id;
				OccurredOn = occurredOn;
			}
		}
	}
}

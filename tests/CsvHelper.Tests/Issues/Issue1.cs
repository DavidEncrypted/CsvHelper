// Copyright 2009-2024 Josh Close
// This file is a part of CsvHelper and is dual licensed under MS-PL and Apache 2.0.
// See LICENSE.txt for details or visit http://www.opensource.org/licenses/ms-pl.html for MS-PL and http://opensource.org/licenses/Apache-2.0 for Apache 2.0.
// https://github.com/JoshClose/CsvHelper
using CsvHelper.Configuration.Attributes;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace CsvHelper.Tests.Issues
{
	// https://github.com/DavidEncrypted/CsvHelper/issues/1
	// Using FormatAttribute on a record field has no effect when writing.
	public class Issue1
	{
		[Fact]
		public void WriteRecords_RecordWithFormatAttribute_UsesFormat()
		{
			var records = new List<Person>
			{
				new Person("Alice", new DateTime(2020, 12, 25)),
			};

			using var writer = new StringWriter();
			using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
			csv.WriteRecords(records);

			var expected = new StringBuilder();
			expected.Append("Name,Birthday\r\n");
			expected.Append("Alice,2020-12-25\r\n");

			Assert.Equal(expected.ToString(), writer.ToString());
		}

		[Fact]
		public void WriteRecords_ClassWithFormatAttributeOnConstructorParam_UsesFormat()
		{
			var records = new List<PersonClass>
			{
				new PersonClass("Bob", new DateTime(2020, 12, 25)),
			};

			using var writer = new StringWriter();
			using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
			csv.WriteRecords(records);

			var expected = new StringBuilder();
			expected.Append("Name,Birthday\r\n");
			expected.Append("Bob,2020-12-25\r\n");

			Assert.Equal(expected.ToString(), writer.ToString());
		}

		[Fact]
		public void ReadAndWriteRecords_RecordWithFormatAttribute_RoundTrips()
		{
			var csvContent = "Name,Birthday\r\nAlice,2020-12-25\r\n";

			using var reader = new StringReader(csvContent);
			using var csvReader = new CsvReader(reader, CultureInfo.InvariantCulture);
			var records = csvReader.GetRecords<Person>().ToList();

			Assert.Single(records);
			Assert.Equal("Alice", records[0].Name);
			Assert.Equal(new DateTime(2020, 12, 25), records[0].Birthday);

			using var writer = new StringWriter();
			using var csvWriter = new CsvWriter(writer, CultureInfo.InvariantCulture);
			csvWriter.WriteRecords(records);

			Assert.Equal(csvContent, writer.ToString());
		}

		public record Person(string Name, [Format("yyyy-MM-dd")] DateTime Birthday);

		public class PersonClass
		{
			public string Name { get; private set; }
			public DateTime Birthday { get; private set; }

			public PersonClass(string name, [Format("yyyy-MM-dd")] DateTime birthday)
			{
				Name = name;
				Birthday = birthday;
			}
		}
	}
}

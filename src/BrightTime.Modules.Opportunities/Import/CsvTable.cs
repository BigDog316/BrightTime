using System.Text;

namespace BrightTime.Modules.Opportunities.Import;

/// <summary>
/// An RFC 4180 reader: quoted fields, doubled quotes, line breaks inside quotes, CRLF or LF.
/// Strict about malformed quoting, because a quote out of place shifts every later column
/// and would import plausible but wrong data.
/// </summary>
public static class CsvTable
{
    /// <summary>The records, or the line (1-based) at which the quoting broke.</summary>
    public static (IReadOnlyList<CsvRecord>? Records, int? BadLine) Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<CsvRecord> records = [];
        List<string> fields = [];
        StringBuilder field = new();
        int line = 1;
        int recordLine = 1;
        int i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        bool quoted = false;
        bool afterQuote = false;
        bool any = false;

        void EndField()
        {
            fields.Add(field.ToString());
            field.Clear();
            afterQuote = false;
        }

        void EndRecord()
        {
            EndField();

            // A blank line is not a record; neither is a line of empty fields left by a spreadsheet.
            if (fields.Exists(f => f.Length > 0))
            {
                records.Add(new CsvRecord(recordLine, [.. fields]));
            }

            fields.Clear();
            any = false;
            recordLine = line;
        }

        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                        afterQuote = true;
                    }
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    field.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0 && !afterQuote:
                    quoted = true;
                    any = true;
                    break;
                case '"':
                    return (null, line);
                case ',':
                    EndField();
                    any = true;
                    break;
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    break;
                case '\r' or '\n':
                    line++;
                    EndRecord();
                    break;
                default:
                    // Only spaces may follow a closing quote before the comma.
                    if (afterQuote && c != ' ')
                    {
                        return (null, line);
                    }

                    if (!afterQuote)
                    {
                        field.Append(c);
                    }

                    any = true;
                    break;
            }
        }

        if (quoted)
        {
            return (null, recordLine);
        }

        if (any || field.Length > 0 || fields.Count > 0)
        {
            EndRecord();
        }

        return (records, null);
    }
}

/// <summary>One record and the line it starts on, for error messages.</summary>
public sealed record CsvRecord(int Line, IReadOnlyList<string> Fields);

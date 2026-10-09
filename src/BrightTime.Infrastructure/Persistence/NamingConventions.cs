using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BrightTime.Infrastructure.Persistence;

/// <summary>
/// Maps CLR names to the schema's snake_case.
/// </summary>
/// <remarks>
/// Applied as a model-finalising pass rather than per-property, so a new
/// entity cannot be forgotten. The CI drift gate compares the model EF
/// produces against <c>db/schema/*.sql</c> in both directions, which turns a
/// naming mistake into a failed build rather than a runtime "column does not
/// exist".
/// </remarks>
public static class NamingConventions
{
    public static void ApplySnakeCaseNames(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (IMutableEntityType entity in modelBuilder.Model.GetEntityTypes())
        {
            if (entity.GetTableName() is { } tableName && !entity.IsOwned())
            {
                entity.SetTableName(ToSnakeCase(tableName));
            }

            foreach (IMutableProperty property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.GetColumnName()));
            }

            foreach (IMutableKey key in entity.GetKeys())
            {
                string? name = key.GetName();
                if (name is not null)
                {
                    key.SetName(ToSnakeCase(name));
                }
            }

            foreach (IMutableForeignKey fk in entity.GetForeignKeys())
            {
                string? name = fk.GetConstraintName();
                if (name is not null)
                {
                    fk.SetConstraintName(ToSnakeCase(name));
                }
            }

            foreach (IMutableIndex index in entity.GetIndexes())
            {
                string? name = index.GetDatabaseName();
                if (name is not null)
                {
                    index.SetDatabaseName(ToSnakeCase(name));
                }
            }
        }
    }

    internal static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char current = name[i];

            if (char.IsUpper(current))
            {
                bool previousIsLower = i > 0 && char.IsLower(name[i - 1]);
                bool previousIsUpperAndNextIsLower =
                    i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]);

                if (i > 0 && name[i - 1] != '_' && (previousIsLower || previousIsUpperAndNextIsLower))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLower(current, CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }
}

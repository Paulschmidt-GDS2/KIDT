using System.Text;

namespace KIDT.Services.Logic;

public static class ToolNameConverter // Wandelt Methodennamen in snake_case für Tool-Registrierung
{
    public static string ToSnakeCase(string name) // PascalCase → snake_case (SearchDocuments → search_documents)
    {
        if (string.IsNullOrEmpty(name)) return name; // Leerer Name unverändert

        StringBuilder result = new StringBuilder();
        result.Append(char.ToLower(name[0])); // Erstes Zeichen klein

        for (int i = 1; i < name.Length; i++) // Restliche Zeichen durchlaufen
        {
            if (char.IsUpper(name[i])) // Großbuchstabe → Wortgrenze
            {
                result.Append('_'); // Trenner einfügen
                result.Append(char.ToLower(name[i])); // Zeichen klein anhängen
            }
            else
            {
                result.Append(name[i]); // Zeichen unverändert anhängen
            }
        }

        return result.ToString();
    }
}

using System.Text.Json.Serialization;
using MyFront.Core.Accounts;

namespace MyFront.Core;

/// <summary>Source-generated JSON (no reflection at startup).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<Account>))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<Import.SavedImport>))]
[JsonSerializable(typeof(Quick.QuickQueryFile))]
[JsonSerializable(typeof(Editors.EditorSet))]
[JsonSerializable(typeof(ConnectionFile))]
internal partial class CoreJson : JsonSerializerContext
{
}

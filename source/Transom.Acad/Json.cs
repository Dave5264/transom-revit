using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.AutoCAD.Geometry;

namespace Transom.Acad;

internal static class Json
{
    private static readonly JsonSerializerOptions Options = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(object? value) => JsonSerializer.Serialize(value, Options);

    public static string Error(string message) => Serialize(new Dictionary<string, object?> { ["ok"] = false, ["error"] = message });

    public static Dictionary<string, object?> Fail(string message) => new() { ["ok"] = false, ["error"] = message };

    // ---- argument readers (args is the "args" object of the bridge request)

    public static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static bool Bool(JsonElement args, string name, bool fallback = false) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : fallback;

    public static int Int(JsonElement args, string name, int fallback) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i
            : fallback;

    public static string[]? StrArray(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return new[] { v.GetString() ?? "" };
        if (v.ValueKind != JsonValueKind.Array) return null;
        return v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString()).ToArray();
    }

    // ---- geometry formatting

    public static double R(double v) => Math.Round(v, 6);
    public static double[] P(Point3d p) => new[] { R(p.X), R(p.Y), R(p.Z) };
    public static double[] P(Point2d p) => new[] { R(p.X), R(p.Y) };
    public static double Deg(double radians) => R(radians * 180.0 / Math.PI);
}

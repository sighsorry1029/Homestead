using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Homestead;

internal static class ZoneBlueprintFileFormat
{
    public const string BlueprintExtension = ".blueprint";
    public const string IconExtension = ".png";

    private const string DefaultCreator = "Homestead";
    private const string DefaultCategory = "Blueprints";
    private const string DefaultPieceCategory = "Building";
    private const string HeaderName = "#Name:";
    private const string HeaderCreator = "#Creator:";
    private const string HeaderDescription = "#Description:";
    private const string HeaderCategory = "#Category:";
    private const string HeaderHomesteadVersion = "#HomesteadVersion:";
    private const string HeaderHomesteadWorld = "#HomesteadWorld:";
    private const string HeaderHomesteadSavedAt = "#HomesteadSavedAt:";
    private const string HeaderHomesteadRadius = "#HomesteadRadius:";
    private const string HeaderHomesteadTerrainContact = "#HomesteadTerrainContact:";
    private const string HeaderSnapPoints = "#SnapPoints";
    private const string HeaderTerrain = "#Terrain";
    private const string HeaderHeight = "#Height:";
    private const string HeaderPaint = "#Paint";
    private const string HeaderPieces = "#Pieces";

    private enum BlueprintSection
    {
        Pieces,
        SnapPoints,
        Terrain,
        Skip
    }

    public static ZoneBlueprintFile ReadFile(string path)
    {
        string fallbackName = Path.GetFileNameWithoutExtension(path);
        return Deserialize(File.ReadAllText(path), fallbackName);
    }

    public static void WriteFile(string path, ZoneBlueprintFile blueprint)
    {
        string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, Serialize(blueprint));
            ReadFile(tempPath);

            if (!File.Exists(path))
            {
                File.Move(tempPath, path);
                return;
            }

            File.Replace(tempPath, path, null);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // A stale temporary file is safer than masking the original write result.
            }
        }
    }

    public static string Serialize(ZoneBlueprintFile blueprint)
    {
        StringBuilder builder = new();
        builder.Append(HeaderName).AppendLine(SanitizeHeaderValue(blueprint.Name));
        builder.Append(HeaderCreator).AppendLine(SanitizeHeaderValue(string.IsNullOrWhiteSpace(blueprint.Creator) ? DefaultCreator : blueprint.Creator));
        builder.Append(HeaderDescription).AppendLine();
        builder.Append(HeaderCategory).AppendLine(DefaultCategory);
        builder.Append(HeaderHomesteadVersion).AppendLine(blueprint.Version.ToString(CultureInfo.InvariantCulture));
        builder.Append(HeaderHomesteadWorld).AppendLine(SanitizeHeaderValue(blueprint.World));
        builder.Append(HeaderHomesteadSavedAt).AppendLine(SanitizeHeaderValue(blueprint.SavedAt));
        builder.Append(HeaderHomesteadRadius).AppendLine(FormatFloat(blueprint.Radius));

        foreach (ZoneBlueprintTerrainContact contact in blueprint.TerrainContacts)
        {
            builder.Append(HeaderHomesteadTerrainContact)
                .Append(FormatFloat(contact.LocalX))
                .Append(';')
                .Append(FormatFloat(contact.LocalY))
                .Append(';')
                .AppendLine(FormatFloat(contact.LocalZ));
        }

        if (blueprint.SnapPoints.Count > 0)
        {
            builder.AppendLine(HeaderSnapPoints);
            foreach (ZoneBlueprintSnapPoint snapPoint in blueprint.SnapPoints)
            {
                if (!ZoneBlueprintCommands.TryReadBlueprintSnapPoint(snapPoint, out UnityEngine.Vector3 localPoint))
                {
                    continue;
                }

                builder.Append(FormatFloat(localPoint.x))
                    .Append(';')
                    .Append(FormatFloat(localPoint.y))
                    .Append(';')
                    .AppendLine(FormatFloat(localPoint.z));
            }
        }

        builder.AppendLine(HeaderPieces);
        foreach (ZoneBlueprintEntry entry in blueprint.Entries)
        {
            builder.Append(SanitizePieceField(entry.Prefab))
                .Append(';')
                .Append(DefaultPieceCategory)
                .Append(';')
                .Append(FormatFloat(ReadArray(entry.LocalPos, 0)))
                .Append(';')
                .Append(FormatFloat(ReadArray(entry.LocalPos, 1)))
                .Append(';')
                .Append(FormatFloat(ReadArray(entry.LocalPos, 2)))
                .Append(';')
                .Append(FormatFloat(ReadArray(entry.LocalRot, 0)))
                .Append(';')
                .Append(FormatFloat(ReadArray(entry.LocalRot, 1)))
                .Append(';')
                .Append(FormatFloat(ReadArray(entry.LocalRot, 2)))
                .Append(';')
                .Append(FormatFloat(ReadArray(entry.LocalRot, 3, 1f)))
                .Append(';')
                .Append(EncodeJsonString(SanitizeAdditionalInfo(entry.Text)))
                .Append(';')
                .Append(FormatFloat(ReadArray(entry.Scale, 0, 1f)))
                .Append(';')
                .Append(FormatFloat(ReadArray(entry.Scale, 1, 1f)))
                .Append(';')
                .AppendLine(FormatFloat(ReadArray(entry.Scale, 2, 1f)));
        }

        return builder.ToString();
    }

    public static ZoneBlueprintFile Deserialize(string text, string fallbackName = "")
    {
        ZoneBlueprintFile blueprint = new()
        {
            Name = fallbackName ?? ""
        };
        BlueprintSection section = BlueprintSection.Pieces;

        using StringReader reader = new(text ?? "");
        string? rawLine;
        while ((rawLine = reader.ReadLine()) != null)
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (TryReadHeader(line, HeaderName, out string name))
            {
                blueprint.Name = string.IsNullOrWhiteSpace(name) ? blueprint.Name : name.Trim();
                continue;
            }

            if (TryReadHeader(line, HeaderCreator, out string creator))
            {
                blueprint.Creator = creator.Trim();
                continue;
            }

            if (TryReadHeader(line, HeaderHomesteadVersion, out string versionText))
            {
                if (int.TryParse(versionText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int version))
                {
                    blueprint.Version = version;
                }
                continue;
            }

            if (TryReadHeader(line, HeaderHomesteadWorld, out string world))
            {
                blueprint.World = world.Trim();
                continue;
            }

            if (TryReadHeader(line, HeaderHomesteadSavedAt, out string savedAt))
            {
                blueprint.SavedAt = savedAt.Trim();
                continue;
            }

            if (TryReadHeader(line, HeaderHomesteadRadius, out string radiusText))
            {
                if (TryParseFloat(radiusText, out float radius))
                {
                    blueprint.Radius = radius;
                }
                continue;
            }

            if (TryReadHeader(line, HeaderHomesteadTerrainContact, out string contactText))
            {
                if (TryParseVector(contactText, out float x, out float y, out float z))
                {
                    blueprint.TerrainContacts.Add(new ZoneBlueprintTerrainContact
                    {
                        LocalX = x,
                        LocalY = y,
                        LocalZ = z
                    });
                }
                continue;
            }

            if (StartsWithHeader(line, HeaderSnapPoints))
            {
                section = BlueprintSection.SnapPoints;
                continue;
            }

            if (StartsWithHeader(line, HeaderTerrain) || StartsWithHeader(line, HeaderHeight) || StartsWithHeader(line, HeaderPaint))
            {
                section = BlueprintSection.Terrain;
                continue;
            }

            if (StartsWithHeader(line, HeaderPieces))
            {
                section = BlueprintSection.Pieces;
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                bool isComment = line.Length == 1 || char.IsWhiteSpace(line[1]);
                if (!isComment)
                {
                    section = BlueprintSection.Skip;
                }

                continue;
            }

            if (section == BlueprintSection.SnapPoints &&
                TryParseVector(line, out float snapX, out float snapY, out float snapZ))
            {
                ZoneBlueprintCommands.TryAddBlueprintSnapPoint(
                    blueprint.SnapPoints,
                    new UnityEngine.Vector3(snapX, snapY, snapZ));
                continue;
            }

            if (section == BlueprintSection.Pieces && TryParsePiece(line, out ZoneBlueprintEntry? entry) && entry != null)
            {
                blueprint.Entries.Add(entry);
            }
        }

        if (string.IsNullOrWhiteSpace(blueprint.Name))
        {
            blueprint.Name = fallbackName ?? "";
        }

        return blueprint;
    }

    private static bool TryParsePiece(string line, out ZoneBlueprintEntry? entry)
    {
        entry = null;
        string[] fields = line.Split(';');
        if (fields.Length < 9)
        {
            return false;
        }

        string prefab = fields[0].Trim();
        if (string.IsNullOrWhiteSpace(prefab) ||
            !TryParseFloat(fields[2], out float px) ||
            !TryParseFloat(fields[3], out float py) ||
            !TryParseFloat(fields[4], out float pz) ||
            !TryParseFloat(fields[5], out float rx) ||
            !TryParseFloat(fields[6], out float ry) ||
            !TryParseFloat(fields[7], out float rz) ||
            !TryParseFloat(fields[8], out float rw))
        {
            return false;
        }

        float sx = 1f;
        float sy = 1f;
        float sz = 1f;
        if (fields.Length > 12)
        {
            TryParseFloat(fields[10], out sx, 1f);
            TryParseFloat(fields[11], out sy, 1f);
            TryParseFloat(fields[12], out sz, 1f);
        }

        entry = new ZoneBlueprintEntry
        {
            Prefab = prefab,
            LocalPos = [px, py, pz],
            LocalRot = [rx, ry, rz, rw],
            Scale = [sx, sy, sz],
            Text = fields.Length > 9 ? DecodeAdditionalInfo(fields[9]) : ""
        };
        return true;
    }

    private static bool TryReadHeader(string line, string header, out string value)
    {
        value = "";
        if (!line.StartsWith(header, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        value = line.Substring(header.Length);
        return true;
    }

    private static bool StartsWithHeader(string line, string header)
    {
        return line.StartsWith(header, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseVector(string text, out float x, out float y, out float z)
    {
        x = 0f;
        y = 0f;
        z = 0f;
        string[] fields = (text ?? "").Split(';');
        return fields.Length >= 3 &&
               TryParseFloat(fields[0], out x) &&
               TryParseFloat(fields[1], out y) &&
               TryParseFloat(fields[2], out z);
    }

    private static bool TryParseFloat(string text, out float value, float defaultValue = 0f)
    {
        text = (text ?? "").Trim().Replace(',', '.');
        if (string.IsNullOrEmpty(text))
        {
            value = defaultValue;
            return true;
        }

        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            float.IsNaN(value) ||
            float.IsInfinity(value))
        {
            value = defaultValue;
            return false;
        }

        return true;
    }

    private static string FormatFloat(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            value = 0f;
        }

        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static float ReadArray(float[]? values, int index, float fallback = 0f)
    {
        return values != null && index >= 0 && index < values.Length ? values[index] : fallback;
    }

    private static string SanitizeHeaderValue(string value)
    {
        return (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static string SanitizePieceField(string value)
    {
        return SanitizeHeaderValue(value).Replace(";", "");
    }

    private static string SanitizeAdditionalInfo(string value)
    {
        return (value ?? "").Replace(";", "").Replace("\0", "");
    }

    private static string DecodeAdditionalInfo(string value)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0 || string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) || value == "\"\"")
        {
            return "";
        }

        return SanitizeAdditionalInfo(value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"'
            ? DecodeJsonString(value)
            : value);
    }

    private static string EncodeJsonString(string value)
    {
        value ??= "";
        StringBuilder builder = new(value.Length + 2);
        builder.Append('"');
        foreach (char character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < ' ')
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static string DecodeJsonString(string value)
    {
        StringBuilder builder = new(value.Length);
        for (int index = 1; index < value.Length - 1; index++)
        {
            char character = value[index];
            if (character != '\\' || index + 1 >= value.Length - 1)
            {
                builder.Append(character);
                continue;
            }

            char escaped = value[++index];
            switch (escaped)
            {
                case '"':
                case '\\':
                case '/':
                    builder.Append(escaped);
                    break;
                case 'b':
                    builder.Append('\b');
                    break;
                case 'f':
                    builder.Append('\f');
                    break;
                case 'n':
                    builder.Append('\n');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case 'u':
                    if (index + 4 < value.Length &&
                        int.TryParse(value.Substring(index + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int codepoint))
                    {
                        builder.Append((char)codepoint);
                        index += 4;
                    }
                    break;
                default:
                    builder.Append(escaped);
                    break;
            }
        }

        return builder.ToString();
    }
}

namespace Achicrack.Core.Adapters;

/// <summary>Minimal read-only INI reader (sections, key=value, ';' and '#' comments).</summary>
internal sealed class IniFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public static IniFile? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length < 1024 * 1024 ? Parse(File.ReadAllLines(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static IniFile Parse(IEnumerable<string> lines)
    {
        var ini = new IniFile();
        var current = ini.Section("");
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                current = ini.Section(line[1..^1].Trim());
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        return ini;
    }

    public string? Get(string section, string key) =>
        _sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : null;

    private Dictionary<string, string> Section(string name)
    {
        if (!_sections.TryGetValue(name, out var s))
        {
            s = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _sections[name] = s;
        }

        return s;
    }
}

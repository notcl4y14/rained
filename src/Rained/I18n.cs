using System.Text.Json;
using KeraLua;

namespace Rained;

struct LocaleData {
    public string name;
    public string codename;
    public string filepath;
}

static class I18n {
    private const string localesDir = "dist/locales";

    private static Dictionary<string, LocaleData> locales = new();
    private static Dictionary<string, string> strings = new();
    private static string? currentLocale;
    private static Lua? luaState = null;

    static I18n()
    {
        luaState = new();

        if (luaState == null)
        {
            Log.Warning("Failed to load Lua state. Cannot load Lua locales");
            return;
        }

        UpdateLocales();

        bool success = LoadLocale("en-US");

        if (!success) {
            Log.Error("Failed to load the en-US locale upon initialization");
        }
    }

    public static Dictionary<string, LocaleData> GetLocales()
    {
        return locales;
    }

    public static string? GetCurrentLocale()
    {
        return currentLocale;
    }

    public static bool LoadLocale(string name)
    {
        bool success = locales.TryGetValue(name, out LocaleData localeData);
        if (!success) return false;

        success = LoadStringsFromLuaFile(localeData.filepath);

        if (success)
        {
            currentLocale = name;
        }

        return success;
    }

    public static string GetString(string key)
    {
        strings.TryGetValue(key, out string? outValue);
        return outValue ?? key;
    }

    public static bool UpdateLocales()
    {
        if (!Directory.Exists(localesDir))
        {
            return false;
        }

        locales.Clear();

        foreach (string dirItem in Directory.GetFileSystemEntries(localesDir))
        {
            if (!File.Exists(dirItem))
            {
                // The item is a directory, so we skip it
                continue;
            }

            LocaleData? nullableLocaleData = RetrieveLocaleDataFromFile(dirItem);

            if (nullableLocaleData == null)
            {
                Log.Warning($"Could not retrieve data from {dirItem}");
                continue;
            }

            LocaleData localeData;
            localeData.name = nullableLocaleData.Value.name;
            localeData.codename = nullableLocaleData.Value.codename;
            localeData.filepath = dirItem;

            locales[localeData.codename] = localeData;
        }

        return true;
    }

    /// <summary>
    /// Reads the file and returns a LocaleData based on the data from the file's contents.
    /// The returned LocaleData has an empty filepath, assign it manually.
    /// </summary>
    static LocaleData? RetrieveLocaleDataFromFile(string filepath)
    {
        if (luaState == null) return null;

        if (!File.Exists(filepath))
        {
            return null;
        }

        string fileContents = File.ReadAllText(filepath);

        // local f = loadstring(fileContents)
        LuaStatus status = luaState.LoadString(fileContents);

        if (status != LuaStatus.OK)
        {
            string error = luaState.ToString(0);
            Log.Error($"I18n LUA: {error}");
            return null;
        }

        // f()
        status = luaState.PCall(0, 0, 0);

        if (status != LuaStatus.OK)
        {
            string error = luaState.ToString(0);
            Log.Error($"I18n LUA: {error}");
            return null;
        }

        // NAME
        LuaType type = luaState.GetGlobal("NAME");

        if (type != LuaType.String)
        {
            Log.Error("I18n LUA: Expected \"NAME\" variable");
            return null;
        }

        string localeName = luaState.ToString(-1);
        luaState.Pop(1);

        // CODENAME
        type = luaState.GetGlobal("CODENAME");

        if (type != LuaType.String)
        {
            Log.Error("I18n LUA: Expected \"CODENAME\" variable");
            return null;
        }

        string localeCodename = luaState.ToString(-1);
        luaState.Pop(1);

        // Clearing Globals
        luaState.PushNil();
        luaState.SetGlobal("NAME");

        luaState.PushNil();
        luaState.SetGlobal("CODENAME");

        luaState.SetTop(0);

        LocaleData localeData;
        localeData.name = localeName;
        localeData.codename = localeCodename;
        localeData.filepath = ""; // Resolved later by the CheckLocales()

        return localeData;
    }

    /// <summary>
    /// Helper function. Reads locale key table and assigns its child keys to the I18n's strings.
    /// </summary>
    static bool ReadTableKey(int tableIndex, string keyPath)
    {
        if (luaState == null) return false;

        luaState.PushNil();
        tableIndex -= 1;

        while (luaState.Next(tableIndex))
        {
            const int keyIndex = -2;
            const int valueIndex = -1;

            if (luaState.Type(keyIndex) == LuaType.Number)
            {
                string value = luaState.ToString(valueIndex);

                if (luaState.ToInteger(keyIndex) == 1)
                {
                    strings[keyPath] = value;
                }
            }
            else if (luaState.Type(keyIndex) != LuaType.String)
            {
                Log.Warning($"I18n LUA: Non-string/table key of type \"{luaState.TypeName(keyIndex)}\" found in the locale table");

                luaState.Pop(1);
                continue;
            }

            // Determine the key suffix: it's either empty or keyPath with a '.' character at the end
            string keySuffix = keyPath != "" ? $"{keyPath}." : "";

            if (luaState.Type(valueIndex) == LuaType.String)
            {
                string key = luaState.ToString(keyIndex);
                string value = luaState.ToString(valueIndex);

                strings[$"{keySuffix}{key}"] = value;
            }
            else if (luaState.Type(valueIndex) == LuaType.Table)
            {
                string key = luaState.ToString(keyIndex);
                ReadTableKey(valueIndex, $"{keySuffix}{key}");
            }

            luaState.Pop(1);
        }

        return true;
    }

    public static bool LoadStringsFromLuaFile(string filepath)
    {
        if (luaState == null) return false;

        if (!File.Exists(filepath))
        {
            return false;
        }

        Log.Information($"Loading localization table from {filepath}");

        string fileContents = File.ReadAllText(filepath);

        // local f = loadstring(fileContents)
        LuaStatus status = luaState.LoadString(fileContents);

        if (status != LuaStatus.OK)
        {
            string error = luaState.ToString(0);
            Log.Error($"I18n LUA: {error}");
            return false;
        }

        // local t = pcall(f)
        status = luaState.PCall(0, 1, 0);

        if (status != LuaStatus.OK)
        {
            string error = luaState.ToString(0);
            Log.Error($"I18n LUA: {error}");
            return false;
        }

        if (luaState.Type(-1) != LuaType.Table)
        {
            Log.Error($"I18n LUA: Expected table, got ${luaState.TypeName(-1)} instead");
            return false;
        }

        strings.Clear();

        ReadTableKey(-1, "");

        // Clearing Globals
        luaState.PushNil();
        luaState.SetGlobal("NAME");

        luaState.PushNil();
        luaState.SetGlobal("CODENAME");

        luaState.SetTop(0);

        return true;
    }
}

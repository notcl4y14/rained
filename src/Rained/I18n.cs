using System.Text.Json;
using KeraLua;

namespace Rained;

struct LocaleData {
    public string name;
    public string codename;
    public int funcref;
}

static class I18n {
    private const string localesDir = "locales";

    private static Dictionary<string, LocaleData> locales = new();
    private static Dictionary<string, string> strings = new();
    private static string? currentLocale;
    private static Lua luaState = new();

    static I18n()
    {
        bool success = LoadLocales();

        if (!success) {
            Log.Error($"Failed to load locales. Make sure that the \"locales\" directory exists at this path: {Boot.AssetsPath}");
        }

        success = ApplyLocale("en-US");

        if (!success) {
            Log.Error("Failed to apply the en-US locale upon initialization");
        }
    }

    public static Dictionary<string, LocaleData> GetLocales()
    {
        return locales;
    }

    /// <summary>
    /// Returns the codename of the current locale.
    /// Returns null if no locale is loaded.
    /// </summary>
    public static string? GetCurrentLocale()
    {
        return currentLocale;
    }

    /// <summary>
    /// Applies a stored locale to the I18n's strings dictionary. The locale is determined by its codename.
    /// Returns true on success, false on failure.
    /// </summary>
    public static bool ApplyLocale(string codename)
    {
        bool success = locales.TryGetValue(codename, out LocaleData localeData);
        if (!success) return false;

        success = LoadStringsFromLocaleData(localeData);

        if (success)
        {
            currentLocale = codename;
        }

        return success;
    }

    /// <summary>
    /// Retrieves a string from the I18n's strings dictionary and returns it.
    /// Returns the requested key instead if the string does not exist.
    /// </summary>
    public static string GetString(string key)
    {
        strings.TryGetValue(key, out string? outValue);
        return outValue ?? key;
    }

    /// <summary>
    /// Retrieves every valid locale file, reads it and stores it into I18n's locales dictionary.
    /// The function may be called again to update the locales, since it automatically clears out the dictionary first
    /// (However, the dictionary is not cleared if it fails to find the locales directory).
    /// Returns true on success, false on failure.
    /// </summary>
    public static bool LoadLocales()
    {
        string localesDirPath = Path.Combine(Boot.AssetsPath, localesDir);
        
        if (!Directory.Exists(localesDirPath))
        {
            return false;
        }

        locales.Clear();

        foreach (string dirItem in Assets.ConfigDirectory.EnumerateFiles(localesDirPath))
        {
            if (!File.Exists(dirItem))
            {
                // The item is a directory, so we skip it
                continue;
            }

            if (Path.GetExtension(dirItem) != ".lua") {
                // The item is not a LUA file, so we skip it
                continue;
            }

            bool success = LoadLocaleFromFile(dirItem, out LocaleData localeData);

            if (!success)
            {
                Log.Warning($"Could not retrieve data from {dirItem}");
                continue;
            }

            locales[localeData.codename] = localeData;
        }

        return true;
    }

    /// <summary>
    /// Reads the file from filepath and modifies localeData's properties based on the data from the file's contents.
    /// Returns true on success, false on failure.
    /// </summary>
    static bool LoadLocaleFromFile(string filepath, out LocaleData localeData)
    {
        localeData = new LocaleData { };
        
        if (!File.Exists(filepath))
        {
            return false;
        }

        string fileContents = File.ReadAllText(filepath);

        // local f = loadstring(fileContents)
        LuaStatus status = luaState.LoadString(fileContents);

        if (status != LuaStatus.OK)
        {
            string error = luaState.ToString(0);
            Log.Error($"I18n LUA: {error}");
            return false;
        }

        // Isolating the function
        luaState.NewTable();
        luaState.SetUpValue(-2, 1);

        int funcRef = luaState.Ref(LuaRegistry.Index);
        LuaType type = luaState.RawGetInteger(LuaRegistry.Index, funcRef);

        if (type != LuaType.Function) {
            Log.Error($"I18n LUA: Expected function");
            return false;
        }

        // f()
        status = luaState.PCall(0, 0, 0);

        if (status != LuaStatus.OK)
        {
            string error = luaState.ToString(0);
            Log.Error($"I18n LUA: {error}");
            return false;
        }

        // Retrieving the stored function from the registry to access its environment
        luaState.RawGetInteger(LuaRegistry.Index, funcRef);
        luaState.GetUpValue(-1, 1);

        // NAME
        type = luaState.GetField(-1, "Name");

        if (type != LuaType.String)
        {
            Log.Error("I18n LUA: Expected \"Name\" variable");
            return false;
        }

        string localeName = luaState.ToString(-1);
        luaState.Pop(1);

        // CODENAME
        type = luaState.GetField(-1, "Codename");

        if (type != LuaType.String)
        {
            Log.Error("I18n LUA: Expected \"Codename\" variable");
            return false;
        }

        string localeCodename = luaState.ToString(-1);
        luaState.Pop(1);
        
        type = luaState.GetField(-1, "Data");

        if (type != LuaType.Function)
        {
            Log.Error($"I18n LUA: Expected global function Data()");
            return false;
        }

        luaState.SetTop(0);

        localeData.name = localeName;
        localeData.codename = localeCodename;
        localeData.funcref = funcRef;
        
        return true;
    }

    /// <summary>
    /// Helper function. Reads locale key table and assigns its child keys to the I18n's strings.
    /// </summary>
    static bool ReadTableKey(int tableIndex, string keyPath)
    {
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

    /// <summary>
    /// Loads strings from the LocaleData data.
    /// Returns true on success, false on failure.
    /// </summary>
    static bool LoadStringsFromLocaleData(LocaleData data)
    {
        Log.Information($"Loading localization table from {data.codename}");

        // f
        LuaType type = luaState.RawGetInteger(LuaRegistry.Index, data.funcref);

        if (type != LuaType.Function)
        {
            Log.Error($"I18n: Expected function");
            return false;
        }

        // local t = pcall(f)
        LuaStatus status = luaState.PCall(0, 1, 0);

        if (status != LuaStatus.OK)
        {
            string error = luaState.ToString(0);
            Log.Error($"I18n LUA: {error}");
            return false;
        }

        // Retrieving the stored function from the registry to access its environment
        luaState.RawGetInteger(LuaRegistry.Index, data.funcref);
        luaState.GetUpValue(-1, 1);

        // Data
        type = luaState.GetField(-1, "Data");

        if (type != LuaType.Function)
        {
            Log.Error($"I18n LUA: Expected global function Data()");
            return false;
        }

        // local a = pcall(Data)
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

        luaState.SetTop(0);

        return true;
    }
}

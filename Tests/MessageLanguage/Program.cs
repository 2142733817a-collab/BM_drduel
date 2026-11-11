using AdimiDuel_Server.Duel;

if (args.Length != 1 || args[0] is not ("en" or "zh"))
    throw new ArgumentException("Pass the expected language (en or zh) for this fresh process.");

bool expectedEnglish = args[0] == "en";
if (AdimiToolsMessageLanguage.IsEnglish != expectedEnglish)
    throw new InvalidOperationException("The environment setting selected the wrong language.");
if (AdimiToolsMessageLanguage.Select("中文", "English") != (expectedEnglish ? "English" : "中文"))
    throw new InvalidOperationException("The selected message text is wrong.");

foreach (string value in new[] { "en", "EN", " English ", "en-US", "en_GB" })
    if (!AdimiToolsMessageLanguage.IsEnglishSetting(value)) throw new InvalidOperationException("Not recognized: " + value);
foreach (string? value in new string?[] { null, "", " ", "zh", "fr", "englishx", "enough" })
    if (AdimiToolsMessageLanguage.IsEnglishSetting(value)) throw new InvalidOperationException("Unexpected English: " + value);

string nativeName = AdimiToolsMessageLanguage.NativeName("{=CAYZDsAj}Legionary", "军团步兵");
if (nativeName != (expectedEnglish ? "Legionary" : "军团步兵"))
    throw new InvalidOperationException("The Native troop fallback was selected incorrectly.");
foreach (string? raw in new string?[] { null, "", "{=bad", "{=id}", "{=id}{NAME}", "{=id}<b>unsafe</b>", "{=id}中文" })
    if (AdimiToolsMessageLanguage.NativeName(raw, "原名") != "原名")
        throw new InvalidOperationException("An unsafe or non-English troop fallback was accepted: " + raw);

// The selected deployment language is fixed once read, even if code inside
// the running process later changes its environment. Real deployments choose
// the language before starting the server process.
Environment.SetEnvironmentVariable(AdimiToolsMessageLanguage.EnvironmentVariable, expectedEnglish ? "zh" : "en");
if (AdimiToolsMessageLanguage.IsEnglish != expectedEnglish)
    throw new InvalidOperationException("The language changed during the same process.");

Console.WriteLine($"PASS: language={args[0]}, setting/fallback and process-lifetime checks.");

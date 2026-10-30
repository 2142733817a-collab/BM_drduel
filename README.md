Code for the Mount & Blade II: Bannerlord 'DR Duel' server which is a copy of the Mount & Blade Warband Duel system.  
The code was not formatted / refactored etc. since it was created out of curiosity and trying around what is possible while staying Native compatible. It was not supposed to be released at all.

Some small parts (messages / reflections) might be copied by krex his Admin tool or from takeo (cRPG).

## FT3 / FT5 / FT7 scene objects

In a scene running this server's Duel mode, place three weapon-hittable objects with collision and give each object exactly one of these tags:

| Tag | Personal duel mode |
| --- | --- |
| `adimi_ft3_anvil` | First to 3 wins |
| `adimi_ft5_anvil` | First to 5 wins |
| `adimi_ft7_anvil` | First to 7 wins |

Hit an object to select its mode. Hit the same mode's object again to return to FT1; hit another mode's object to switch directly. An object with multiple FT tags is disabled. Use the same hittable entity arrangement as the existing FT7 anvils; entity names do not select the mode. Existing FT7 tags and the script name remain supported.

Both players must select the same win limit to request and accept a duel. The limit is fixed when the duel is accepted and cannot be changed during the series. FT3 means first to 3 wins, not best of 3. The existing `!ft 1` through `!ft 10` commands remain available and follow the same matching rule. FT3, FT5 and FT7 series all announce the final result.

These tags require the server code; Native alone does not implement them. The example scenes are unchanged, so place and label the three objects in your own map before testing in game.

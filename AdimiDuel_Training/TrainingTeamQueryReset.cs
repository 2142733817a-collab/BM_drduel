using System;
using System.Reflection;
using TaleWorlds.MountAndBlade;

namespace AdimiDuel_Training
{
    internal static class TrainingTeamQueryReset
    {
        // TeamQuerySystem.Expire does not clear these 1.4.8 lifetime values.
        // Validate all members before any mutation and fail the training round
        // if a future game version changes their layout.
        internal static void Reset(Team team, float now)
        {
            if (team == null || team.QuerySystem == null) throw new InvalidOperationException("训练队伍的查询系统不可用。");
            const BindingFlags properties = BindingFlags.Instance | BindingFlags.Public;
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type type = typeof(TeamQuerySystem);
            PropertyInfo deaths = type.GetProperty("DeathCount", properties);
            PropertyInfo ranged = type.GetProperty("DeathByRangedCount", properties);
            FieldInfo maxRanged = type.GetField("_maxUnderRangedAttackRatio", fields);
            if (deaths == null || deaths.PropertyType != typeof(int) || deaths.GetSetMethod(true) == null ||
                ranged == null || ranged.PropertyType != typeof(int) || ranged.GetSetMethod(true) == null ||
                maxRanged == null || maxRanged.FieldType != typeof(QueryData<float>))
                throw new InvalidOperationException("当前游戏版本的队伍查询统计不兼容，训练已停止。");
            var query = team.QuerySystem;
            var maxValue = maxRanged.GetValue(query) as QueryData<float>;
            if (maxValue == null) throw new InvalidOperationException("无法重置队伍查询统计，训练已停止。");
            deaths.SetValue(query, 0, null);
            ranged.SetValue(query, 0, null);
            maxValue.SetValue(0f, now);
            query.Expire();
        }
    }
}

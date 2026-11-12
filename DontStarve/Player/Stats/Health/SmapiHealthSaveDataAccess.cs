using System;
using StardewModdingAPI;

namespace DontStarve.Player.Stats.Health;

/// <summary>
/// SMAPI 存档 API 的薄适配层（镜像 SmapiHungerSaveDataAccess）；
/// 异常转成稳定 reason，由 HealthPersistenceStore 决定是否只读熔断。
/// </summary>
internal sealed class SmapiHealthSaveDataAccess : IHealthSaveDataAccess
{
    private readonly IDataHelper data;

    internal SmapiHealthSaveDataAccess(IDataHelper data)
    {
        this.data = data;
    }

    public HealthRawReadResult Read(string key)
    {
        try
        {
            var value = data.ReadSaveData<object>(key);
            return value is null
                ? HealthRawReadResult.Missing()
                : HealthRawReadResult.Success(value.ToString() ?? string.Empty);
        }
        catch (Exception ex)
        {
            return HealthRawReadResult.Error($"smapi-read-exception:{ex.GetType().Name}");
        }
    }

    public HealthRawWriteResult Write(string key, HealthSaveData data)
    {
        try
        {
            this.data.WriteSaveData(key, data);
            return HealthRawWriteResult.Succeeded();
        }
        catch (Exception ex)
        {
            return HealthRawWriteResult.Error($"smapi-write-exception:{ex.GetType().Name}");
        }
    }
}

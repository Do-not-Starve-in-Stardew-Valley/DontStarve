using System;
using StardewModdingAPI;

namespace DontStarve.Player.Stats.Hunger;

/// <summary>
/// SMAPI 存档 API 的薄适配层；异常转成稳定 reason，由 HungerPersistenceStore 决定是否熔断写回。
/// </summary>
internal sealed class SmapiHungerSaveDataAccess : IHungerSaveDataAccess
{
    private readonly IDataHelper data;

    internal SmapiHungerSaveDataAccess(IDataHelper data)
    {
        this.data = data;
    }

    public HungerRawReadResult Read(string key)
    {
        try
        {
            var value = data.ReadSaveData<object>(key);
            return value is null
                ? HungerRawReadResult.Missing()
                : HungerRawReadResult.Success(value.ToString() ?? string.Empty, value);
        }
        catch (Exception ex)
        {
            return HungerRawReadResult.Error($"smapi-read-exception:{ex.GetType().Name}");
        }
    }

    public HungerRawWriteResult Copy(string key, HungerRawReadResult source)
    {
        try
        {
            if (source.NativeValue is null)
                return HungerRawWriteResult.Error("raw-save-value-is-unavailable");

            data.WriteSaveData(key, source.NativeValue);
            return HungerRawWriteResult.Succeeded();
        }
        catch (Exception ex)
        {
            return HungerRawWriteResult.Error($"smapi-write-exception:{ex.GetType().Name}");
        }
    }

    public HungerRawWriteResult WriteV2(string key, HungerSaveData saveData)
    {
        try
        {
            data.WriteSaveData(key, saveData);
            return HungerRawWriteResult.Succeeded();
        }
        catch (Exception ex)
        {
            return HungerRawWriteResult.Error($"smapi-write-exception:{ex.GetType().Name}");
        }
    }
}

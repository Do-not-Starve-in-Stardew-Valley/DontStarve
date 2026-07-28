using System;
using StardewModdingAPI;

namespace DontStarve.Player.Stats.Sanity;

/// <summary>
/// SMAPI save-data 的薄适配层。异常只转换成不含玩家正文的稳定 reason，
/// 是否继续写入由 <see cref="SanityPersistenceStore"/> 统一决定。
/// </summary>
internal sealed class SmapiSanitySaveDataAccess : ISanitySaveDataAccess
{
    private readonly IDataHelper data;

    internal SmapiSanitySaveDataAccess(IDataHelper data)
    {
        this.data = data;
    }

    public SanityRawReadResult Read(string key)
    {
        try
        {
            var value = data.ReadSaveData<object>(key);
            if (value is null)
                return SanityRawReadResult.Missing();

            // SMAPI 以自身 JSON token 返回 object；保留这个原对象用于 backup 直拷，
            // 避免跨 serializer 重建 legacy 时丢掉未知字段。
            return SanityRawReadResult.Success(value.ToString(), value);
        }
        catch (Exception ex)
        {
            return SanityRawReadResult.Error(
                $"smapi-read-exception:{ex.GetType().Name}"
            );
        }
    }

    public SanityRawWriteResult Copy(string key, SanityRawReadResult source)
    {
        try
        {
            if (source.NativeValue is null)
                return SanityRawWriteResult.Error("raw-save-value-is-unavailable");

            data.WriteSaveData(key, source.NativeValue);
            return SanityRawWriteResult.Succeeded();
        }
        catch (Exception ex)
        {
            return SanityRawWriteResult.Error(
                $"smapi-write-exception:{ex.GetType().Name}"
            );
        }
    }

    public SanityRawWriteResult WriteV2(string key, SanitySaveData saveData)
    {
        try
        {
            data.WriteSaveData(key, saveData);
            return SanityRawWriteResult.Succeeded();
        }
        catch (Exception ex)
        {
            return SanityRawWriteResult.Error(
                $"smapi-write-exception:{ex.GetType().Name}"
            );
        }
    }
}

#nullable enable

using System;
using System.Collections.Generic;
using StardewModdingAPI;

namespace Common.ConfigurationServices;

/// <summary>The locally verified Content Patcher 2.9.1 mod-token surface.</summary>
public interface IContentPatcherApi
{
    /// <summary>Register a token under the provider mod's automatic namespace.</summary>
    void RegisterToken(
        IManifest mod,
        string name,
        Func<IEnumerable<string>?> getValue
    );
}

namespace TrustScore.Tests.TestSupport;

/// <summary>
/// Real OpenTimestamps material, captured on 2026-10-09: the answers of three public calendars to
/// one SHA-256 digest, and the .ots file the reference implementation (python-opentimestamps)
/// builds from them, with the message each pending attestation commits to as it computes them.
/// </summary>
public static class OtsVectors
{
    /// <summary>SHA-256 of "trustscoreagent ots test vector\n".</summary>
    public const string Digest = "92118c14134d054006368a227bdc3309b30a3a40386b822e0e5d4f1d3eecec45";

    public const string AlicePool =
        "f00898cdd1b16b2743a208f0104254e5a0dbac880bf806b996acffd52e08f120ce4ab4464a6a4c4e33c59c0e59b356849e8e92e29a127bfa5c935569d56870f308f020b8ab22b6b1ca7ae5b8a36426d4ba8151b3f683792230e4907b389e5b748b5f0308f1046ac8b1e4f0081994148b05894c290083dfe30d2ef90c8e2e2d68747470733a2f2f616c6963652e6274632e63616c656e6461722e6f70656e74696d657374616d70732e6f7267";

    public const string BobPool =
        "f00809b157f7da8bec0d08f010996de24e521f7deaf26aaa292f8cc48808f0207ac46edb56cffcabc3f842e0b6484768c6189aa37909e38f499eafecfd70880808f1209b4c96ab4120909acd6ccc3f5ea51745e01812408ec3e9ed17c7fec4aaf503fb08f020a63b0f6dc04a3e6de5d3d497362fab64d4f0a5ce83de79df312e65cf4ae1c50b08f1046ac8b1e5f008c7d0f34af1c9e7910083dfe30d2ef90c8e2c2b68747470733a2f2f626f622e6274632e63616c656e6461722e6f70656e74696d657374616d70732e6f7267";

    public const string EternityWallPool =
        "f010e177595da810a33babc4d94b800686a608f1207e0124e49c95023c9c1715ccf7b28aca5bb79925a70b80112425da17e2f5147e08f020bae0252d1cca8cf2a4dbc0d303adc6221f270f973fb1be670de810ff1959a25508f020193a48f2bc6fc39d3f85ad5e919dbd2e58919b70dad25d18684a2ec9bad122ae08f1046ac8b1e6f00857bed714cd517feb0083dfe30d2ef90c8e292868747470733a2f2f66696e6e65792e63616c656e6461722e657465726e69747977616c6c2e636f6d";

    /// <summary>python-opentimestamps' DetachedTimestampFile of the three answers merged.</summary>
    public const string ReferenceFile =
        "004f70656e54696d657374616d7073000050726f6f6600bf89e2e884e89294010892118c14134d054006368a227bdc3309b30a3a40386b822e0e5d4f1d3eecec45fff00809b157f7da8bec0d08f010996de24e521f7deaf26aaa292f8cc48808f0207ac46edb56cffcabc3f842e0b6484768c6189aa37909e38f499eafecfd70880808f1209b4c96ab4120909acd6ccc3f5ea51745e01812408ec3e9ed17c7fec4aaf503fb08f020a63b0f6dc04a3e6de5d3d497362fab64d4f0a5ce83de79df312e65cf4ae1c50b08f1046ac8b1e5f008c7d0f34af1c9e7910083dfe30d2ef90c8e2c2b68747470733a2f2f626f622e6274632e63616c656e6461722e6f70656e74696d657374616d70732e6f7267fff00898cdd1b16b2743a208f0104254e5a0dbac880bf806b996acffd52e08f120ce4ab4464a6a4c4e33c59c0e59b356849e8e92e29a127bfa5c935569d56870f308f020b8ab22b6b1ca7ae5b8a36426d4ba8151b3f683792230e4907b389e5b748b5f0308f1046ac8b1e4f0081994148b05894c290083dfe30d2ef90c8e2e2d68747470733a2f2f616c6963652e6274632e63616c656e6461722e6f70656e74696d657374616d70732e6f7267f010e177595da810a33babc4d94b800686a608f1207e0124e49c95023c9c1715ccf7b28aca5bb79925a70b80112425da17e2f5147e08f020bae0252d1cca8cf2a4dbc0d303adc6221f270f973fb1be670de810ff1959a25508f020193a48f2bc6fc39d3f85ad5e919dbd2e58919b70dad25d18684a2ec9bad122ae08f1046ac8b1e6f00857bed714cd517feb0083dfe30d2ef90c8e292868747470733a2f2f66696e6e65792e63616c656e6461722e657465726e69747977616c6c2e636f6d";

    /// <summary>Calendar URL to the message its pending attestation commits to (per the reference implementation).</summary>
    public static readonly IReadOnlyDictionary<string, string> PendingMessages = new Dictionary<string, string>
    {
        ["https://alice.btc.calendar.opentimestamps.org"] =
            "6ac8b1e4289f82fdbeac11b865ab603666edd03ca741915f6ee46586de55155b179abf631994148b05894c29",
        ["https://bob.btc.calendar.opentimestamps.org"] =
            "6ac8b1e5c4380c2e3244cf0e8d797c04d5db2ea15bdbc7ba080d924e3d2cbe4990ef0674c7d0f34af1c9e791",
        ["https://finney.calendar.eternitywall.com"] =
            "6ac8b1e65b1e8522f12442252eabe845f131beac5bd9562748cb98f3fdf87993cd25b98857bed714cd517feb",
    };

    public static byte[] Bytes(string hex) => Convert.FromHexString(hex);
}

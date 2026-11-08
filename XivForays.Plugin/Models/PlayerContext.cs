namespace XivMate.DataGathering.Forays.Dalamud.Models;

// Kind 19 PlayerContext (task 2026-11-08-1300): the local player's foray context while recording. Numbers and bools
// only. Group members are job / level / role / foray level / effective foray level / synced only: never names,
// content ids, object ids or worlds. Player statuses are an allow-list (Core.ContextRules.IsAllowedStatusName).

internal sealed record PlayerContext
{
    public int SituationVersion { get; init; } = 1;

    /// <summary>Bumped whenever capture logic changes (Core.ContextRules.CaptureRevision), so data stays comparable.</summary>
    public int CaptureRevision { get; init; }

    /// <summary>1 start of visit, 2 change, 3 periodic (60 s).</summary>
    public byte Reason { get; init; }

    /// <summary>Changed parts since the previous context record (Core.ContextRules bits).</summary>
    public int Changed { get; init; }

    /// <summary>Framework.ClientTime.EorzeaTime (server-synced ET seconds): with observedAtMs it gives the clock offset.</summary>
    public long? EorzeaTime { get; init; }

    /// <summary>Framework.UtcTime.Timestamp (server-synced Unix seconds).</summary>
    public long? FrameworkUtc { get; init; }

    /// <summary>The active public content director's start timestamp: the same for every capturer in this instance.</summary>
    public long? InstanceStartTs { get; init; }

    /// <summary>The active director's content row id.</summary>
    public uint? DirectorRowId { get; init; }

    /// <summary>UIState.PublicInstance.InstanceId (numbered instances; 0 is sent as null).</summary>
    public uint? PublicInstanceId { get; init; }

    /// <summary>The local player's own current world (World row id), for instance matching. Never another player's.</summary>
    public uint? CurrentWorldId { get; init; }

    public byte ClassJobId { get; init; }
    public short Level { get; init; }
    public short SyncedLevel { get; init; }
    public bool LevelSynced { get; init; }
    public float HpPct { get; init; }
    public float? MpPct { get; init; }
    public bool Dead { get; init; }
    public bool Mounted { get; init; }
    public bool InCombat { get; init; }

    public EurekaContext? Eureka { get; init; }

    /// <summary>Bozja: [currentExperience, neededExperience].</summary>
    public uint[]? Bozja { get; init; }

    public OccultContext? Occult { get; init; }

    /// <summary>UIState.Chain [remainingSeconds, maxSeconds]; null when no chain.</summary>
    public float[]? Chain { get; init; }

    /// <summary>The last "EXP chain #n" game notice number this visit, and when (Unix ms).</summary>
    public uint? LastChainNumber { get; init; }

    public long? LastChainAtMs { get; init; }

    /// <summary>Own group size including self (1 = solo).</summary>
    public int GroupSize { get; init; }

    public bool Alliance { get; init; }

    /// <summary>
    /// Other members: [[classJobId, level, role, forayLevel, effectiveForayLevel, synced]] (role from the ClassJob
    /// sheet), sorted; no names or ids. Foray columns: Eureka elemental level / effective elemental level / synced;
    /// Bozja resistance rank / 0 / 0; Occult knowledge level / effective knowledge level / synced; 0 when not loaded
    /// (Core.ContextRules.MemberRow). Rows had only the first three columns before captureRevision 3.
    /// </summary>
    public int[][] Group { get; init; } = [];

    /// <summary>Allow-listed own statuses: [[statusId, param, timeLeftDs]].</summary>
    public int[][] Statuses { get; init; } = [];
}

/// <summary>PublicContentEureka state: the magia board and elemental EXP.</summary>
internal sealed record EurekaContext
{
    public byte MaxElementalLevel { get; init; }
    public uint Experience { get; init; }
    public uint ExperienceNeeded { get; init; }

    /// <summary>Magia board: [fire, ice, wind, earth, lightning, water].</summary>
    public int[] Board { get; init; } = [];

    public byte Magicite { get; init; }
    public byte MagiaAether { get; init; }
    public ushort MagiaAetherCharge { get; init; }
}

/// <summary>PublicContentOccultCrescent state.</summary>
internal sealed record OccultContext
{
    public uint Knowledge { get; init; }
    public uint KnowledgeNeeded { get; init; }
    public byte KnowledgeLevelSync { get; init; }
    public byte SupportJob { get; init; }
    public int[] SupportJobLevels { get; init; } = [];
    public ushort Silver { get; init; }
    public ushort Gold { get; init; }
}

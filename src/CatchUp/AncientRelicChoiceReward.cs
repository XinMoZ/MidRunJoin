using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;

namespace MidRunJoin.CatchUp;

/// <summary>
/// Native-synced "choose 1 of 3 Ancient relics" reward backed by STS2's own
/// <see cref="RelicSelectCmd.FromChooseARelicScreen"/> (<c>NChooseARelicSelection</c> + <c>PlayerChoiceSynchronizer</c>).
/// Each instance corresponds to a specific <see cref="AncientEventModel"/> visited earlier in the run.
/// </summary>
public sealed class AncientRelicChoiceReward : Reward
{
    private readonly AncientEventModel? _ancient;
    private readonly List<RelicModel> _candidates;

    public AncientEventModel? Ancient => _ancient;
    public IReadOnlyList<RelicModel> Candidates => _candidates;
    public RelicModel? ClaimedRelic { get; private set; }

    protected override RewardType RewardType => RewardType.Relic;
    public override int RewardsSetIndex => 3;
    public override bool IsPopulated => _candidates.Count > 0;

    public override LocString Description =>
        _ancient?.Title ?? new LocString("gameplay_ui", "CHOOSE_RELIC_HEADER");

    protected override IEnumerable<IHoverTip> ExtraHoverTips =>
        _candidates.SelectMany(r => r.HoverTips);

    public AncientRelicChoiceReward(AncientEventModel? ancient, IReadOnlyList<RelicModel> candidates, Player player)
        : base(player)
    {
        if (candidates == null || candidates.Count == 0)
            throw new ArgumentException("AncientRelicChoiceReward requires at least 1 candidate relic.", nameof(candidates));
        _ancient = ancient;
        _candidates = candidates.ToList();
        foreach (var r in _candidates)
            r.AssertMutable();
    }

    public override void Populate()
    {
        // Candidates are pre-rolled deterministically from the specific Ancient's pool.
    }

    public override Control? CreateIcon()
    {
        if (TestMode.IsOn)
            return null;

        var textureRect = new TextureRect
        {
            Texture = _candidates[0].BigIcon,
            Material = (ShaderMaterial)PreloadManager.Cache.GetMaterial("res://materials/ui/relic_mat.tres").Duplicate(deep: true),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize
        };
        _candidates[0].UpdateTexture(textureRect);
        textureRect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        return textureRect;
    }

    protected override async Task<bool> OnSelect()
    {
        RelicModel? chosen = _candidates.Count == 1
            ? _candidates[0]
            : await RelicSelectCmd.FromChooseARelicScreen(Player, _candidates);

        if (chosen == null)
            return false;

        Log.Info($"[MidRunJoin] Player {Player.NetId} chose {chosen.Id.Entry} from {_ancient?.Id.Entry ?? "ANCIENT"} 1-of-{_candidates.Count} reward.");
        ClaimedRelic = await RelicCmd.Obtain(chosen, Player);
        return true;
    }

    public override void MarkContentAsSeen()
    {
        foreach (var relic in _candidates)
            SaveManager.Instance.MarkRelicAsSeen(relic);
    }
}

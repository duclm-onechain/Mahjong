using System;
using Cinemachine;
using Sirenix.OdinInspector;
using System.Collections;
using UnityEngine;
using System.Collections.Generic;
using DG.Tweening;
using static Cinemachine.DocumentationSortingAttribute;
using System.Linq;

public class GameplayManager : Singleton<GameplayManager>
{
    public EGamePlayState State => state;
    [SerializeField, ReadOnly] protected EGamePlayState state;
    public EGamePlayState LastState { get; private set; }
    public bool winGame { get; private set; }
    public int CurrentLevel { get; set; }
    public int LevelTime { get; private set; }
    private bool hasRoundEnded;
    public int Score { get; private set; }
    public int CurrentCombo { get; private set; }
    public PackageResource PackReward { get; private set; }

    [SerializeField] private float comboWindowSeconds = 8f;
    public float ComboWindowSeconds => comboWindowSeconds;

    private float lastMatchTime = float.NegativeInfinity;
    public IEnumerator IEInit()
    {
        DebugCustom.LogColor("Init Level");
        SetState(EGamePlayState.Cinematic);

        IAchievementController.Instance.UpdateAchievementProgress(EAchievementType.LevelPlay);

        LevelTime = 180;
        CurrentLevel = IPlayerInfoController.Instance.CurrentLevel();
        yield return new WaitUntil(() => ResolutionManager.Instance);
        yield return new WaitUntil(() => ResolutionManager.Instance.IsInitilized);

        if (GameManager.Instance.GameType != EGameType.Campaign)
        {
            yield return new WaitUntil(() => UIManager.Instance.uIGameplay);
        }
        else
        {
            Debug.Log("[Mahjong] Skip legacy UiGameplay wait in GameplayManager.IEInit for Campaign.");
        }

        if (GameManager.Instance.GameType == EGameType.Endless)
            IAchievementController.Instance.UpdateAchievementProgress(EAchievementType.EndlessPlay);

        if (GameManager.Instance.GameType != EGameType.Campaign && UIManager.Instance.uIGameplay != null)
        {
            UIManager.Instance.uIGameplay.Initialize();
        }

        StartGame();
        TigerForge.EventManager.StartListening(Constant.EVENT_TIMER_TICK, OnTick);
        TigerForge.EventManager.EmitEvent(Constant.EVENT_LEVEL_INITED);
    }
    public void StartGame()
    {
        hasRoundEnded = false;
        winGame = false;
        ResetCombo();
        SetState(EGamePlayState.Running);
    }
    private void Update()
    {
#if UNITY_EDITOR
        if (Input.GetKeyDown(KeyCode.X))
        {
            for (int i = 0; i < 10; i++)
            {
                OnTick();
            }
        }
#endif
    }
    void OnTick()
    {
        if (state == EGamePlayState.Running && LevelTime > 0)
        {
            if (LevelTime <= 0)
                EndGame(false);
        }

    }
    public void SetState(EGamePlayState _state)
    {
        state = _state;
        DebugCustom.LogColor("GamePlayState", State);
        if (state != EGamePlayState.Pause)
        {
            LastState = state;
            Time.timeScale = 1;
        }
        else
            Time.timeScale = 0;

        TigerForge.EventManager.EmitEvent(Constant.ON_GAME_STATE_CHANGE);
    }

    public void EndGame(bool win)
    {
        if (hasRoundEnded || state == EGamePlayState.GameOver)
            return;

        hasRoundEnded = true;
        DebugCustom.LogColor("End Game");
        SetGameOver(win);
        if (winGame)
        {
            int completedLevel = CurrentLevel;
            MahjongOut3D.Managers.LevelManager levelManager = UnityEngine.Object.FindAnyObjectByType<MahjongOut3D.Managers.LevelManager>();
            if (levelManager != null && levelManager.CurrentLevelIndex >= 0)
            {
                completedLevel = levelManager.CurrentLevelIndex + 1;
            }

            IPlayerInfoController.Instance.WinLevel();
            IAchievementController.Instance.UpdateAchievementProgress(EAchievementType.LevelWin);
            if (GameManager.Instance.GameType == EGameType.Endless)
                IAchievementController.Instance.UpdateAchievementProgress(EAchievementType.WinEndlessStage);
            IAchievementController.Instance.UpdateAchievementProgress(EAchievementType.LevelWin);

            PackReward = CreateWinRewardPackage();

            if (!PopupRate.CheckAndShowBeforeWinPopup(completedLevel, () => UIManager.Instance.ShowPopupWinGame(), 7))
            {
                UIManager.Instance.ShowPopupWinGame();
            }
        }
        else
        {
            PackReward = null;
            if (GameManager.Instance.GameType == EGameType.Endless)
            {
                PackReward = CreateLoseRewardPackage();
            }

            UIManager.Instance.ShowPopupLoseGame();
        }
    }
    public void SetGameOver(bool win)
    {
        if (state == EGamePlayState.GameOver && winGame == win)
            return;

        winGame = win;
        ResetCombo();
        SetState(EGamePlayState.GameOver);
    }

    public void ClaimCurrentReward(int multiplier, EResourceFrom resourceFrom)
    {
        if (PackReward == null)
        {
            return;
        }

        CreateScaledPackage(PackReward, multiplier).ReceiveResource(resourceFrom);
        PackReward = null;
    }

    public void ReviveFromAds(int reviveTimeBonus = 30)
    {
        PackReward = null;
        if (LevelTime <= 0)
        {
            LevelTime = reviveTimeBonus;
        }

        winGame = false;
        SetState(EGamePlayState.Running);
    }

    PackageResource CreateWinRewardPackage()
    {
        PackageResource package = new PackageResource();
        package.AddResource(new CommonResource(ECommonResource.Coin, 15));
        package.AddResource(new CommonResource(ECommonResource.Gem, 10));
        package.AddResource(new CommonResource(ECommonResource.ActivePoint, 1));
        return package;
    }

    PackageResource CreateLoseRewardPackage()
    {
        PackageResource package = new PackageResource();
        package.AddResource(new CommonResource(ECommonResource.Coin, Score));
        package.AddResource(new CommonResource(ECommonResource.ActivePoint, Score / 10));
        return package;
    }

    PackageResource CreateScaledPackage(PackageResource source, int multiplier)
    {
        PackageResource package = new PackageResource();
        if (source == null)
        {
            return package;
        }

        int safeMultiplier = Mathf.Max(1, multiplier);
        foreach (var resource in source.lstResource)
        {
            GameResource clone = CloneResource(resource, safeMultiplier);
            if (clone != null)
            {
                package.AddResource(clone);
            }
        }

        return package;
    }

    GameResource CloneResource(GameResource resource, int multiplier)
    {
        switch (resource)
        {
            case CommonResource commonResource:
                return new CommonResource(commonResource.resourceType, commonResource.resourceValue * multiplier);
            case VirtualResource virtualResource:
                return new VirtualResource(virtualResource.resourceType, virtualResource.resourceValue * multiplier);
            case ExpireableResource expireableResource:
                return new ExpireableResource(expireableResource.resourceType, expireableResource.resourceValue * multiplier);
            case ContentActiveResource contentActiveResource:
                return new ContentActiveResource(contentActiveResource.resourceType);
            default:
                return resource != null ? GameResource.GetResource(resource.GetRewardDataString()) : null;
        }
    }
    public void RegisterSuccessfulMatch(int baseScore = 10)
    {
        float now = Time.unscaledTime;
        if (CurrentCombo > 0 && now - lastMatchTime <= comboWindowSeconds)
        {
            CurrentCombo++;
        }
        else
        {
            CurrentCombo = 1;
        }

        lastMatchTime = now;
        int comboBonus = Mathf.Max(0, CurrentCombo - 1) * (Mathf.Max(5, baseScore / 2));
        Score += baseScore + comboBonus;

        var gameplayHud = UnityEngine.Object.FindAnyObjectByType<MahjongOut3D.UI.GameplayHudView>(FindObjectsInactive.Include);
        if (gameplayHud != null)
        {
            gameplayHud.SetComboTrayGlow(CurrentCombo);
            if (CurrentCombo > 1)
            {
                gameplayHud.ShowComboText(CurrentCombo);
            }
        }
    }

    public void ResetCombo()
    {
        CurrentCombo = 0;
        lastMatchTime = float.NegativeInfinity;

        var gameplayHud = UnityEngine.Object.FindAnyObjectByType<MahjongOut3D.UI.GameplayHudView>(FindObjectsInactive.Include);
        gameplayHud?.ResetComboTrayGlow();
    }

    public void OnClick(Vector3 pos)
    {
        DebugCustom.LogColor("OnClick", pos);
    }
}

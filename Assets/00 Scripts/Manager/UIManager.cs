using Sirenix.OdinInspector;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using WE.UI;

public class UIManager : Singleton<UIManager>
{
    public UiDialog uIDialog;
    public SafeArea uISafeZone;

    public List<UIBase> lstOpenningUI;
    [System.NonSerialized] private Dictionary<string, UIBase> dicUsedUI;

    public UiHome uIHome;
    public UiGameplay uIGameplay;

    int frameFlyingCount;
    float delayHackCount;
    int currentHackCount;
    public bool Initialized { get; set; }
    List<string> lstWaitFlying = new List<string>();
    List<System.Action> lstPendingUI = new List<System.Action>();
    private void Start()
    {
        TigerForge.EventManager.StartListening(Constant.EVENT_TIMER_TICK, OnTick);
    }
    void OnTick()
    {
        if (Initialized)
        {
            if (lstPendingUI.Count > 0)
            {
                foreach (var item in lstPendingUI)
                {
                    item?.Invoke();
                }
                lstPendingUI.Clear();
            }
        }
    }
    IEnumerator IEWaitSafeZone()
    {
        while (uISafeZone == null)
        {
            yield return null;
        }
    }
    IEnumerator IEWaitUIHome()
    {
        while (uIHome == null)
        {
            yield return null;
        }
    }

    IEnumerator IEWaitUIGameplay()
    {
        while (uIGameplay == null)
        {
            yield return null;
        }
        yield return null;
    }
    public IEnumerator IEHomeInit()
    {
        lstOpenningUI = new List<UIBase>();
        dicUsedUI = new Dictionary<string, UIBase>();
        if (uIDialog != null)
            uIDialog.gameObject.SetActive(false);
        yield return StartCoroutine(IEWaitSafeZone());
        yield return StartCoroutine(IEWaitUIHome());
        uIHome.InitHome();
        Initialized = true;
    }
    public IEnumerator IEGamgeInit()
    {
        lstOpenningUI = new List<UIBase>();
        dicUsedUI = new Dictionary<string, UIBase>();
        if (uIDialog != null)
            uIDialog.gameObject.SetActive(false);
        yield return StartCoroutine(IEWaitSafeZone());

        if (GameManager.Instance != null && GameManager.Instance.GameType == EGameType.Campaign)
        {
            Debug.Log("[Mahjong] Skip legacy UIManager.IEGamgeInit wait for Campaign.");
            Initialized = true;
            yield break;
        }

        yield return StartCoroutine(IEWaitUIGameplay());
        Initialized = true;
    }
    public void HomeInit()
    {
    }
    public void Close(UIBase uI)
    {
        DebugCustom.LogColor("Close popup", uI.name);
        if (lstOpenningUI.Contains(uI))
        {
            lstOpenningUI.Remove(uI);
        }
        if (GameplayManager.Instance)
        {
            if (lstOpenningUI.Count == 0 && GameplayManager.Instance.State == EGamePlayState.Pause)
            {
                GameplayManager.Instance.SetState(GameplayManager.Instance.LastState);
            }
        }
    }

    protected virtual void Update()
    {
        frameFlyingCount = 0;
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Back();
        }
        if (delayHackCount > 0)
        {
            delayHackCount -= Time.deltaTime;
            if (delayHackCount <= 0)
            {
                currentHackCount = 0;
            }
        }
    }
    public void CloseAllOldPopup()
    {
        if (lstOpenningUI.Count > 1)
        {
            int count = lstOpenningUI.Count - 1;
            while (count > 0)
            {
                UIBase ui = lstOpenningUI[count - 1];
                if (ui.allowBackKey)
                    ui.Hide();
                count--;
            }
        }
    }
    public void CloseAllPopup()
    {
        if (lstOpenningUI.Count > 0)
        {
            int count = lstOpenningUI.Count;
            while (count > 0)
            {
                UIBase ui = lstOpenningUI[count - 1];
                if (ui.allowBackKey)
                    ui.Hide();
                count--;
            }
        }
    }
    public virtual void Back()
    {
        DebugCustom.LogColor("Escape");
        if (lstOpenningUI.Count > 0)
        {
            UIBase ui = lstOpenningUI[lstOpenningUI.Count - 1];
            DebugCustom.LogColor(ui.name);
            if (ui.allowBackKey)
                ui.Hide();
        }
    }

    public virtual UIBase GetUI(string name)
    {
        if (dicUsedUI.ContainsKey(name))
            return dicUsedUI[name];
        UIBase _uiBase = null;

        if (uISafeZone != null)
        {
            Transform existing = uISafeZone.transform.Find(name);
            if (existing != null)
            {
                _uiBase = existing.GetComponent<UIBase>();
                if (_uiBase != null)
                {
                    dicUsedUI.Add(name, _uiBase);
                    return _uiBase;
                }
            }
        }

        _uiBase = Instantiate(GetUIByPath(name), uISafeZone.transform);
        _uiBase.transform.localPosition = Vector3.zero;
        _uiBase.transform.localScale = Vector3.one;
        _uiBase.gameObject.SetActive(false);
        dicUsedUI.Add(name, _uiBase);
        return _uiBase;
    }
    public virtual UIBase GetUIByPath(string name)
    {
        string resourcePath = $"01 Prefabs/UI/{name}";
        return Resources.Load<UIBase>(resourcePath);
    }
    #region UI Action
    #region Flying Object

    public void ShowCommonObjectFly(ECommonResource resouceType, long count, float delay)
    {
        StartCoroutine(IEDelayCommonObject(resouceType, count, delay));
    }
    IEnumerator IEDelayCommonObject(ECommonResource resouceType, long count, float delay)
    {
        string key = resouceType.ToString();
        if (lstWaitFlying.Contains(key))
            yield break;
        lstWaitFlying.Add(key);
        yield return new WaitUntil(() => uIHome != null);
        yield return new WaitUntil(() => !LoadingPanel.Instance.Playing);
        Transform parent = null;
        switch (resouceType)
        {
            case ECommonResource.Coin:
                parent = uIHome.coinBar;
                break;
            case ECommonResource.Energy:
                parent = uIHome.heartBar;
                break;
            default:
                break;
        }
        if (count > 10)
            count = 10;
        for (int i = 0; i < count; i++)
        {
            yield return new WaitForSeconds(0.1f);
            UIFlyingObject obj = ObjectPooler.Spawn(DataSystem.Instance.dataGamePrefabs.uIFlyingObject, transform.position + (Vector3)Random.insideUnitCircle * 1, parent);
            obj.StartAnim(DataSystem.Instance.dataSprites.dicCommomSprites[EUIResourceResolution.x100][resouceType], delay);
        }

        lstWaitFlying.Remove(key);
    }
    public void ShowIngameObjectFly(EItemIngame itemType)
    {
        //UIFlyingObject obj = ObjectPooler.Spawn(DataSystem.Instance.dataGamePrefabs.uIFlyingObject, uIGameplay.iconAva.transform.position, PlayerIngame.Instance.transform);
        //obj.StartAnim(DataSystem.Instance.dataSprites.dicItemIngameSprites[itemType], 0);
    }
    public void PendingAction(System.Action action)
    {
        lstPendingUI.Add(action);
    }
    #endregion
    public void ShowDialog(string txt)
    {
        uIDialog.ShowDialog(txt);
        DebugCustom.LogColor(txt);
    }
    [Button()]
    public void ShowUIBase()
    {
        UIBase ui = GetUI("UI Base");
        ui.Show();
    }
    public void ShowPopupReward(PackageResource resource)
    {
        PopupShowReward ui = GetUI("Popup Show Reward") as PopupShowReward;
        ui.Show(resource);
    }
    public void ShowPopupRevive()
    {
        UIBase ui = GetUI("Popup Revive");
        ui.Show();
    }
    public void ShowPopupPauseGame()
    {
        UIBase ui = GetUI("Popup Pause Game");
        ui.Show();
    }
    public void ShowPopupEndGame()
    {
        if (GameplayManager.Instance != null && GameplayManager.Instance.winGame)
        {
            ShowPopupWinGame();
            return;
        }

        ShowPopupLoseGame();
    }
    public void ShowPopupWinGame()
    {
        UIBase ui = GetUI("Popup Win");
        if (ui != null && ui.gameObject.activeSelf)
            return;
        ui?.Show();
    }
    public void ShowPopupLoseGame()
    {
        UIBase ui = GetUI("Popup Loss");
        if (ui != null && ui.gameObject.activeSelf)
            return;
        ui?.Show();
    }
    public void ShowPopupBooster()
    {
        UIBase ui = GetUI("Popup Booster");
        ui.Show();
    }
    public void ShowPopupTeam()
    {
        UIBase ui = GetUI("Popup Team");
        ui.Show();
    }
    public void ShowPopupGun()
    {
        UIBase ui = GetUI("Popup Gun");
        ui.Show();
    }
    public void ShowPopupDailyReward()
    {
        UIBase ui = GetUI("Popup Daily Reward");
        ui.Show();
    }
    public void ShowPopupCoinReward()
    {
        UIBase ui = GetUI("Popup Coin Reward");
        ui.Show();
    }
    public void ShowPopupNoAds()
    {
        UIBase ui = GetUI("Popup No Ads");
        ui.Show();
    }
    public void ShowPopupStreak()
    {
        UIBase ui = GetUI("Popup Streak");
        ui.Show();
    }
    public void ShowPopupWeaponChest()
    {
        UIBase ui = GetUI("Popup Weapon Chest");
        ui.Show();
    }
    public void ShowPopupInfoWallet()
    {
        UIBase ui = GetUI("Popup Info Wallet");
        ui.Show();
    }
    public void ShowPopupInventory()
    {
        UIBase ui = GetUI("Popup Inventory");
        ui.Show();
    }
    public void ShowPopupCoppySeedPhrase()
    {
        UIBase ui = GetUI("Popup Coppy Seed Phrase");
        ui.Show();
    }
    public void ShowPopupConfirm(System.Action actionOk, System.Action actionNo = null, string txtDesc = "Confirm Action?", string txtTile = "Warning", EButtonColor okColor = EButtonColor.Yellow, string txtBtnOk = "OK", EButtonColor noColor = EButtonColor.Blue, string txtBtnNo = "No")
    {
        PopupConfirmAction ui = GetUI("Popup Confirm Action") as PopupConfirmAction;
        ui.ShowConfirmAction(actionOk, actionNo, txtDesc, txtTile, okColor, txtBtnOk, noColor, txtBtnNo);
    }
    public void ShowPopupWarning(System.Action actionClose, string txtDesc, string txtTile = "Warning", EButtonColor okColor = EButtonColor.Yellow, string txtBtnOk = "OK")
    {
        PopupConfirmAction ui = GetUI("Popup Confirm Action") as PopupConfirmAction;
        ui.ShowWarning(actionClose, txtDesc, txtTile, okColor, txtBtnOk);
    }
    public void ShowPopupController()
    {
        UIBase ui = GetUI("Popup Controller");
        ui.Show();
    }
    public void ShowPopupDailyShop()
    {
        UIBase ui = GetUI("Popup Daily Shop");
        ui.Show();
    }
    public void ShowPopupLeaderboard()
    {
        UIBase ui = GetUI("Popup Leaderboard");
        ui.Show();
    }
    public void ShowPopupUserInfo()
    {
        UIBase ui = GetUI("Popup User Info");
        ui.Show();
    }
    public void ShowPopupMessage()
    {
        UIBase ui = GetUI("Popup Noti Message");
        ui.Show();
    }
    public void ShowPopupTimeout()
    {
        UIBase ui = GetUI("Popup Timeout");
        ui.Show();
    }
    public void ShowPopupTooltipResource(Vector3 pos, GameResource resource)
    {
        PopupTooltipResource ui = GetUI("Popup Tooltip Resource") as PopupTooltipResource;
        ui.Show(pos, resource);
    }
    public void ShopPopupTooltipPackage(Vector3 pos, PackageResource pack)
    {
        PopupTooltipPackage ui = GetUI("Popup Tooltip Package") as PopupTooltipPackage;
        ui.Show(pos, pack);
    }
    public void ShowPopupQuest()
    {
        UIBase ui = GetUI("Popup Quest");
        if (ui != null)
        {
            ui.Show();
        }
    }

    public void ShowPopupDailyQuest()
    {
        UIBase ui = GetUI("Popup Daily Task");
        if (ui != null)
        {
            ui.Show();
        }
    }

    public void ShowPopupLuckyWheel()
    {
        UIBase ui = GetUI("Popup Lucky Wheel");
        ui.Show();
    }
    public void ShowPopupSetting()
    {
        UIBase ui = GetUI("Popup Setting");
        ui.Show();
    }
    public void ShowPopupIAPHistory()
    {
        UIBase ui = GetUI("Popup IAP History");
        ui.Show();
    }
    public void ShowPopupInviteFriend()
    {
        UIBase ui = GetUI("Popup Invite Friend");
        ui.Show();
    }
    public void ShowPopupPartnerReferal()
    {
        UIBase ui = GetUI("Popup Partner Referal");
        ui.Show();
    }    
    public void ShowPopupMailBox()
    {
        UIBase ui = GetUI("Popup Mail Box");
        ui.Show();
    }
    public void ShowPopupRate(System.Action onClosed = null)
    {
        PopupRate ui = GetUI("Popup Rate") as PopupRate;
        if (ui != null && ui.gameObject.activeSelf)
            return;

        if (ui != null)
        {
            if (onClosed != null)
                ui.Show(onClosed);
            else
                ui.Show();
        }
    }
    #endregion
}

using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class PopupRate : UIBase
{
    [Header("UI References")]
    public Button btnRate;
    public Button btnClose;
    public TextMeshProUGUI txtTitle;
    public TextMeshProUGUI txtDesc;
    public TextMeshProUGUI txtRateButton;
    public Transform starsParent;
    public List<Image> listStarImages = new List<Image>();

    [Header("Star Sprites")]
    public Sprite activeStarSprite;
    public Sprite inactiveStarSprite;

    [Header("Config")]
    [SerializeField] private int defaultRating = 5;
    [SerializeField] private int minStarToOpenStore = 4;
    [SerializeField] private string customStoreUrl = "";
    [SerializeField] private int targetLevel = 7;

    public int CurrentRating => currentRating;
    private int currentRating = 5;
    private bool isTransitioning;
    private Action onCloseCallback;

    public const string PREF_KEY_HAS_RATED = "GAME_HAS_RATED";
    public const string PREF_KEY_SHOWN_RATE = "GAME_SHOWN_RATE_LEVEL_";

    public void Show(Action onClosed)
    {
        this.onCloseCallback = onClosed;
        Show();
    }

    public override void Show()
    {
        if (gameObject.activeSelf)
            return;

        isTransitioning = false;
        DebugCustom.LogColor("Show popup", gameObject.name);

        if (hackObj != null)
            hackObj.SetActive(GameManager.Instance != null && GameManager.Instance.IsTester);
        if (blockPanel != null)
            blockPanel.SetActive(false);

        gameObject.SetActive(true);

        if (UIManager.Instance != null)
        {
            if (!UIManager.Instance.lstOpenningUI.Contains(this))
                UIManager.Instance.lstOpenningUI.Add(this);
        }

        transform.SetAsLastSibling();
        CacheReferences();
        BindButtons();
        BindStars();
        SetRating(defaultRating);
    }

    public override void AfterHideAction()
    {
        base.AfterHideAction();
        TriggerCloseCallback();
    }

    public override void OnDisable()
    {
        isTransitioning = false;
        UnbindButtons();
        base.OnDisable();
        TriggerCloseCallback();
    }

    private void TriggerCloseCallback()
    {
        if (onCloseCallback != null)
        {
            Action callback = onCloseCallback;
            onCloseCallback = null;
            callback.Invoke();
        }
    }

    public void CacheReferences()
    {
        if (buttonClose == null)
            buttonClose = FindButton("Close");

        if (btnClose == null)
            btnClose = buttonClose;

        if (btnRate == null)
        {
            btnRate = FindButton("Rate");
            if (btnRate == null)
                btnRate = FindButton("Button");
        }

        if (txtTitle == null)
        {
            txtTitle = FindText("Having Fun");
            if (txtTitle == null)
                txtTitle = FindText("Are You");
        }

        if (txtDesc == null)
        {
            txtDesc = FindText("Show Us");
            if (txtDesc == null)
                txtDesc = FindText("love");
        }

        if (txtRateButton == null && btnRate != null)
            txtRateButton = btnRate.GetComponentInChildren<TextMeshProUGUI>(true);

        if (starsParent == null)
            starsParent = FindDeepChild(transform, "Stars");

        if (starsParent != null && (listStarImages == null || listStarImages.Count == 0))
        {
            listStarImages = new List<Image>();
            for (int i = 0; i < starsParent.childCount; i++)
            {
                Image img = starsParent.GetChild(i).GetComponent<Image>();
                if (img != null)
                    listStarImages.Add(img);
            }
        }

        // Borrow existing active sprite from first star if not explicitly set
        if (activeStarSprite == null && listStarImages != null && listStarImages.Count > 0 && listStarImages[0] != null)
        {
            activeStarSprite = listStarImages[0].sprite;
        }
    }

    private void BindButtons()
    {
        if (btnRate != null)
        {
            btnRate.onClick.RemoveListener(OnClickRate);
            btnRate.onClick.AddListener(OnClickRate);
        }

        if (btnClose != null)
        {
            btnClose.onClick.RemoveListener(OnClickClose);
            btnClose.onClick.AddListener(OnClickClose);
        }

        if (buttonClose != null && buttonClose != btnClose)
        {
            buttonClose.onClick.RemoveListener(OnClickClose);
            buttonClose.onClick.AddListener(OnClickClose);
        }
    }

    private void UnbindButtons()
    {
        if (btnRate != null)
            btnRate.onClick.RemoveListener(OnClickRate);

        if (btnClose != null)
            btnClose.onClick.RemoveListener(OnClickClose);

        if (buttonClose != null)
            buttonClose.onClick.RemoveListener(OnClickClose);
    }

    private void BindStars()
    {
        if (listStarImages == null)
            return;

        for (int i = 0; i < listStarImages.Count; i++)
        {
            int starIndex = i + 1;
            Image starImg = listStarImages[i];
            if (starImg == null)
                continue;

            starImg.raycastTarget = true;
            Button btn = starImg.GetComponent<Button>();
            if (btn == null)
            {
                btn = starImg.gameObject.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
            }

            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() => OnClickStar(starIndex));
        }
    }

    public void SetRating(int rating)
    {
        currentRating = Mathf.Clamp(rating, 1, listStarImages != null && listStarImages.Count > 0 ? listStarImages.Count : 5);

        if (listStarImages == null)
            return;

        for (int i = 0; i < listStarImages.Count; i++)
        {
            Image starImg = listStarImages[i];
            if (starImg == null)
                continue;

            bool isActive = (i < currentRating);
            if (isActive)
            {
                if (activeStarSprite != null)
                    starImg.sprite = activeStarSprite;
                starImg.color = Color.white;
            }
            else
            {
                if (inactiveStarSprite != null)
                {
                    starImg.sprite = inactiveStarSprite;
                    starImg.color = Color.white;
                }
                else
                {
                    // Fallback visual: dimmed star
                    if (activeStarSprite != null)
                        starImg.sprite = activeStarSprite;
                    starImg.color = new Color(1f, 1f, 1f, 0.35f);
                }
            }
        }
    }

    public void OnClickStar(int starIndex)
    {
        SetRating(starIndex);
    }

    public void OnClickRate()
    {
        if (isTransitioning)
            return;

        isTransitioning = true;
        MarkRated();
        MarkShownRate(targetLevel);

        if (currentRating >= minStarToOpenStore)
        {
            OpenStore();
        }
        else
        {
            DebugCustom.LogColor("Rating submitted with stars:", currentRating);
        }

        Hide();
    }

    public void OnClickClose()
    {
        if (isTransitioning)
            return;

        isTransitioning = true;
        MarkShownRate(targetLevel);
        Hide();
    }

    public void OpenStore()
    {
        if (!string.IsNullOrEmpty(customStoreUrl))
        {
            Application.OpenURL(customStoreUrl);
            return;
        }

        string appId = Application.identifier;
#if UNITY_ANDROID
        Application.OpenURL($"market://details?id={appId}");
#elif UNITY_IOS
        Application.OpenURL($"itms-apps://itunes.apple.com/app/id{appId}");
#else
        Application.OpenURL($"https://play.google.com/store/apps/details?id={appId}");
#endif
    }

    private Button FindButton(string objectName)
    {
        Transform target = FindDeepChild(transform, objectName);
        return target != null ? target.GetComponent<Button>() : null;
    }

    private TextMeshProUGUI FindText(string contains)
    {
        TextMeshProUGUI[] texts = GetComponentsInChildren<TextMeshProUGUI>(true);
        foreach (TextMeshProUGUI text in texts)
        {
            if (text != null && text.text != null && text.text.Contains(contains))
                return text;
        }
        return null;
    }

    private Transform FindDeepChild(Transform parent, string childName)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name == childName)
                return child;

            Transform result = FindDeepChild(child, childName);
            if (result != null)
                return result;
        }
        return null;
    }

    #region Static Helper Methods

    public static bool HasRated()
    {
        return PlayerPrefs.GetInt(PREF_KEY_HAS_RATED, 0) == 1;
    }

    public static void MarkRated()
    {
        PlayerPrefs.SetInt(PREF_KEY_HAS_RATED, 1);
        PlayerPrefs.Save();
    }

    public static bool HasShownRate(int level)
    {
        return PlayerPrefs.GetInt(PREF_KEY_SHOWN_RATE + level, 0) == 1;
    }

    public static void MarkShownRate(int level)
    {
        PlayerPrefs.SetInt(PREF_KEY_SHOWN_RATE + level, 1);
        PlayerPrefs.Save();
    }

    public static void ResetRatePrefs(int level = 7)
    {
        PlayerPrefs.DeleteKey(PREF_KEY_HAS_RATED);
        PlayerPrefs.DeleteKey(PREF_KEY_SHOWN_RATE + level);
        PlayerPrefs.DeleteKey(PREF_KEY_SHOWN_RATE + 8);
        PlayerPrefs.Save();
    }

    public static bool ShouldShowAtLevel(int level, int targetLevel = 7)
    {
        if (level != targetLevel)
            return false;

        if (HasRated())
            return false;

        if (HasShownRate(level))
            return false;

        return true;
    }

    public static bool CheckAndShowBeforeWinPopup(int completedLevel, Action onClosed, int targetLevel = 7)
    {
        if (!ShouldShowAtLevel(completedLevel, targetLevel))
            return false;

        if (UIManager.Instance != null)
        {
            UIManager.Instance.ShowPopupRate(onClosed);
            return true;
        }

        return false;
    }

    public static bool CheckAndShowAtLevel(int level, int targetLevel = 7)
    {
        if (!ShouldShowAtLevel(level, targetLevel))
            return false;

        if (UIManager.Instance != null)
        {
            UIManager.Instance.ShowPopupRate();
            return true;
        }

        return false;
    }

    #endregion
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Cysharp.Threading.Tasks;
using Duckov.Crops;
using Duckov.Economy;
using Duckov.Economy.UI;
using Duckov.Quests;
using Duckov.UI;
using Duckov.UI.Animations;
using Duckov.Utilities;
using ItemStatsSystem;
using SodaCraft.Localizations;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace TooManyDuckovItems
{
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        public const string ItemSourceMerchantID = "ItemSource_Viewer";
        private const string ShopGameObjectName = "ItemSourceViewer";
        
        // 配置选项：是否启用场景分析（默认禁用，因为会卡顿）
        private const bool ENABLE_SCENE_ANALYSIS = false;
        
        // 物品获取方式映射
        private Dictionary<int, List<string>> _itemSourceMap = new Dictionary<int, List<string>>();
        
        // 已添加到商店中的物品ID
        private List<int> _validItemIds = new List<int>();
        
        // 进度UI相关
        private GameObject _progressPanel;
        private TextMeshProUGUI _progressText;
        private Image _progressBar;
        private bool _isAnalyzing = false;
        private bool _sceneAnalysisStarted = false;

        protected override void OnAfterSetup()
        {
            SceneManager.sceneLoaded -= OnAfterSceneInit;
            SceneManager.sceneLoaded += OnAfterSceneInit;
            
            Debug.Log("[ItemSourceViewer] Mod loaded!");
        }

        protected override void OnBeforeDeactivate()
        {
            SceneManager.sceneLoaded -= OnAfterSceneInit;
        }

        // 场景加载回调
        void OnAfterSceneInit(Scene scene, LoadSceneMode mode)
        {
            Debug.Log($"[ItemSourceViewer] 加载场景: {scene.name}");

            if (scene.name == "Base_SceneV2")
            {
                // 构建物品来源映射（先构建基础数据）
                _itemSourceMap = BuildSourceMap();
                
                // 启动协程延迟创建UI和分析场景物品
                StartCoroutine(DelayedSetup());

                // 添加UI事件监听
                var fadeGroup = StockShopView.Instance.GetComponent<FadeGroup>();
                fadeGroup.OnShowComplete -= OnFadeGroupShowComplete;
                fadeGroup.OnShowComplete += OnFadeGroupShowComplete;
            }
        }

        IEnumerator DelayedSetup()
        {
            // 延迟1秒
            yield return new WaitForSeconds(1f);

            var find = GameObject.Find("Buildings/SaleMachine");
            if (find != null)
            {
                // 克隆售货机
                var itemSourceViewer = Instantiate(find.gameObject);
                itemSourceViewer.transform.SetParent(find.transform.parent, true);
                itemSourceViewer.name = ShopGameObjectName;
                // 调试用 -7.4 0 -83
                //itemSourceViewer.transform.position = new Vector3(-7.4f, 0f, -83f);
               
                // 设置位置（放在超级售货机旁边）
                itemSourceViewer.transform.position = new Vector3(-23f, 0f, -68.5f);
                
                var shopTransform = itemSourceViewer.transform.Find("PerkWeaponShop");
                var stockShop = InitShopItems(shopTransform);

                // 修改模型
                UpdateModel(itemSourceViewer);

                itemSourceViewer.SetActive(true);
                Debug.Log("[ItemSourceViewer] 物品来源查看器已激活");

                // 刷新商店物品（先显示基础数据）
                RefreshShop(stockShop);
                
                // 根据配置决定是否启用场景分析
                if (ENABLE_SCENE_ANALYSIS)
                {
                    // 延迟10秒后再开始场景分析，避免与游戏初始化冲突
                    yield return new WaitForSeconds(10f);
                    
                    Debug.Log("[ItemSourceViewer] 准备开始场景分析...");
                    Debug.LogWarning("[ItemSourceViewer] 场景分析可能导致短暂卡顿，请耐心等待");
                    
                    // 创建进度UI
                    CreateProgressUI();

                    // 开始异步分析场景（后台进行）
                    _sceneAnalysisStarted = true;
                    AnalyzeScenesAsync().Forget();
                }
                else
                {
                    Debug.LogWarning("[ItemSourceViewer] 场景分析已禁用（ENABLE_SCENE_ANALYSIS=false）");
                    Debug.LogWarning("[ItemSourceViewer] 宝箱掉落物品信息将不可用");
                    Debug.LogWarning("[ItemSourceViewer] 如需启用，请修改 ModBehaviour.cs 中的 ENABLE_SCENE_ANALYSIS 常量为 true");
                }
            }
            else
            {
                Debug.LogWarning("[ItemSourceViewer] 未找到 Buildings/SaleMachine");
            }
        }

        // 初始化商店物品
        StockShop? InitShopItems(Transform? shopTransform)
        {
            if (shopTransform != null)
            {
                var stockShop = shopTransform.GetComponent<StockShop>();
                if (stockShop != null)
                {
                    stockShop.entries.Clear();
                    
                    // 修改商人ID
                    var merchantIDField = typeof(StockShop).GetField("merchantID",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (merchantIDField != null)
                    {
                        merchantIDField.SetValue(stockShop, ItemSourceMerchantID);
                    }

                    // 检查是否已添加映射
                    var isAdded = false;
                    var merchantProfiles = StockShopDatabase.Instance.merchantProfiles;
                    foreach (var profile in merchantProfiles)
                    {
                        if (profile.merchantID == ItemSourceMerchantID)
                        {
                            isAdded = true;
                            break;
                        }
                    }

                    // 未添加则创建映射
                    if (!isAdded)
                    {
                        _validItemIds.Clear();
                        var allItemEntries = ItemAssetsCollection.Instance.entries;
                        var merchantProfile = new StockShopDatabase.MerchantProfile();
                        merchantProfile.merchantID = ItemSourceMerchantID;
                        
                        foreach (var itemEntry in allItemEntries)
                        {
                            // 过滤无效物品
                            if (itemEntry.prefab.Icon != null && itemEntry.prefab.Icon.name != "cross")
                            {
                                var entry = new StockShopDatabase.ItemEntry();
                                entry.typeID = itemEntry.typeID;
                                entry.maxStock = 999; // 设置大量库存表示"展示"而非"销售"
                                entry.forceUnlock = true;
                                entry.priceFactor = 0f; // 价格为0（不可购买）
                                entry.possibility = -1f;
                                entry.lockInDemo = false;
                                merchantProfile.entries.Add(entry);
                                _validItemIds.Add(entry.typeID);
                            }
                        }

                        // 添加mod物品
                        var dynamicDicField = typeof(ItemAssetsCollection).GetField("dynamicDic",
                            BindingFlags.NonPublic | BindingFlags.Static);
                        if (dynamicDicField != null)
                        {
                            var dynamicDic = dynamicDicField.GetValue(ItemAssetsCollection.Instance) as
                                Dictionary<int, ItemAssetsCollection.DynamicEntry>;
                            if (dynamicDic != null)
                            {
                                foreach (var kv in dynamicDic)
                                {
                                    var itemId = kv.Key;
                                    if (!_validItemIds.Contains(itemId))
                                    {
                                        var dynamicEntry = kv.Value;
                                        if (dynamicEntry.prefab.Icon != null && dynamicEntry.prefab.Icon.name != "cross")
                                        {
                                            var entry = new StockShopDatabase.ItemEntry();
                                            entry.typeID = dynamicEntry.typeID;
                                            entry.maxStock = 999;
                                            entry.forceUnlock = true;
                                            entry.priceFactor = 0f;
                                            entry.possibility = -1f;
                                            entry.lockInDemo = false;
                                            merchantProfile.entries.Add(entry);
                                            _validItemIds.Add(entry.typeID);
                                        }
                                    }
                                }
                            }
                        }

                        merchantProfiles.Add(merchantProfile);
                    }

                    // 调用初始化方法
                    var initializeEntriesMethod = typeof(StockShop).GetMethod("InitializeEntries",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (initializeEntriesMethod != null)
                    {
                        try
                        {
                            initializeEntriesMethod.Invoke(stockShop, null);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[ItemSourceViewer] 调用 InitializeEntries 失败: {ex.Message}");
                        }
                    }

                    return stockShop;
                }
            }

            return null;
        }

        // 刷新商店
        void RefreshShop(StockShop? stockShop)
        {
            if (stockShop == null)
                return;

            var refreshMethod = typeof(StockShop).GetMethod("DoRefreshStock",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (refreshMethod != null)
            {
                try
                {
                    refreshMethod.Invoke(stockShop, null);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ItemSourceViewer] 刷新商店失败: {ex.Message}");
                }
            }

            var lastTimeField = typeof(StockShop).GetField("lastTimeRefreshedStock",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (lastTimeField != null)
            {
                try
                {
                    lastTimeField.SetValue(stockShop, DateTime.UtcNow.ToBinary());
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ItemSourceViewer] 设置时间戳失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 异步分析所有场景中的宝箱和生成器
        /// </summary>
        async UniTask AnalyzeScenesAsync()
        {
            _isAnalyzing = true;
            
            try
            {
                Debug.Log("[ItemSourceViewer] 开始分析场景宝箱和生成器...");
                
                // 创建分析器
                var analyzer = new ItemSourceAnalyzer(_itemSourceMap);
                
                // 设置进度回调
                analyzer.OnProgress = (current, total, sceneName) =>
                {
                    UpdateProgress(current, total, sceneName);
                };
                
                // 设置完成回调
                analyzer.OnComplete = (itemsFound) =>
                {
                    Debug.Log($"[ItemSourceViewer] 场景分析完成！总计发现 {itemsFound} 个物品来源");
                    HideProgress();
                };
                
                // 开始分析
                await analyzer.AnalyzeAllScenesAsync();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ItemSourceViewer] 场景分析失败: {ex.Message}\n{ex.StackTrace}");
                HideProgress();
            }
            finally
            {
                _isAnalyzing = false;
            }
        }

        /// <summary>
        /// 创建进度显示UI
        /// </summary>
        void CreateProgressUI()
        {
            try
            {
                // 创建Canvas
                var canvasObj = new GameObject("ItemSourceAnalyzerProgressCanvas");
                var canvas = canvasObj.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 9999; // 确保在最上层
                
                var canvasScaler = canvasObj.AddComponent<CanvasScaler>();
                canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                canvasScaler.referenceResolution = new Vector2(1920, 1080);
                
                canvasObj.AddComponent<GraphicRaycaster>();

                // 创建背景遮罩
                var bgPanel = new GameObject("Background");
                bgPanel.transform.SetParent(canvasObj.transform, false);
                var bgRect = bgPanel.AddComponent<RectTransform>();
                bgRect.anchorMin = Vector2.zero;
                bgRect.anchorMax = Vector2.one;
                bgRect.sizeDelta = Vector2.zero;
                
                var bgImage = bgPanel.AddComponent<Image>();
                bgImage.color = new Color(0, 0, 0, 0.7f);

                // 创建进度面板
                _progressPanel = new GameObject("ProgressPanel");
                _progressPanel.transform.SetParent(canvasObj.transform, false);
                
                var panelRect = _progressPanel.AddComponent<RectTransform>();
                panelRect.anchorMin = new Vector2(0.5f, 0.5f);
                panelRect.anchorMax = new Vector2(0.5f, 0.5f);
                panelRect.pivot = new Vector2(0.5f, 0.5f);
                panelRect.sizeDelta = new Vector2(600, 200);
                
                var panelImage = _progressPanel.AddComponent<Image>();
                panelImage.color = new Color(0.1f, 0.1f, 0.1f, 0.95f);
                
                // 添加圆角效果（可选）
                var panelOutline = _progressPanel.AddComponent<Outline>();
                panelOutline.effectColor = new Color(0.4f, 0.8f, 1f);
                panelOutline.effectDistance = new Vector2(2, 2);

                // 创建标题文本
                var titleObj = new GameObject("Title");
                titleObj.transform.SetParent(_progressPanel.transform, false);
                var titleRect = titleObj.AddComponent<RectTransform>();
                titleRect.anchorMin = new Vector2(0, 0.7f);
                titleRect.anchorMax = new Vector2(1, 0.95f);
                titleRect.offsetMin = new Vector2(20, 0);
                titleRect.offsetMax = new Vector2(-20, 0);
                
                var titleText = titleObj.AddComponent<TextMeshProUGUI>();
                titleText.fontSize = 28;
                titleText.fontStyle = FontStyles.Bold;
                titleText.alignment = TextAlignmentOptions.Center;
                titleText.color = new Color(0.4f, 0.8f, 1f);
                
                if (LocalizationManager.CurrentLanguage == SystemLanguage.ChineseSimplified ||
                    LocalizationManager.CurrentLanguage == SystemLanguage.ChineseTraditional)
                {
                    titleText.text = "正在分析场景宝箱...";
                }
                else
                {
                    titleText.text = "Analyzing Scene Lootboxes...";
                }

                // 创建进度文本
                var progressTextObj = new GameObject("ProgressText");
                progressTextObj.transform.SetParent(_progressPanel.transform, false);
                var progressTextRect = progressTextObj.AddComponent<RectTransform>();
                progressTextRect.anchorMin = new Vector2(0, 0.4f);
                progressTextRect.anchorMax = new Vector2(1, 0.65f);
                progressTextRect.offsetMin = new Vector2(20, 0);
                progressTextRect.offsetMax = new Vector2(-20, 0);
                
                _progressText = progressTextObj.AddComponent<TextMeshProUGUI>();
                _progressText.fontSize = 18;
                _progressText.alignment = TextAlignmentOptions.Center;
                _progressText.color = Color.white;
                _progressText.text = "准备中...";

                // 创建进度条背景
                var progressBarBg = new GameObject("ProgressBarBackground");
                progressBarBg.transform.SetParent(_progressPanel.transform, false);
                var progressBarBgRect = progressBarBg.AddComponent<RectTransform>();
                progressBarBgRect.anchorMin = new Vector2(0.1f, 0.15f);
                progressBarBgRect.anchorMax = new Vector2(0.9f, 0.35f);
                progressBarBgRect.sizeDelta = Vector2.zero;
                
                var progressBarBgImage = progressBarBg.AddComponent<Image>();
                progressBarBgImage.color = new Color(0.2f, 0.2f, 0.2f, 1f);

                // 创建进度条
                var progressBarObj = new GameObject("ProgressBar");
                progressBarObj.transform.SetParent(progressBarBg.transform, false);
                var progressBarRect = progressBarObj.AddComponent<RectTransform>();
                progressBarRect.anchorMin = Vector2.zero;
                progressBarRect.anchorMax = new Vector2(0, 1);
                progressBarRect.pivot = new Vector2(0, 0.5f);
                progressBarRect.sizeDelta = Vector2.zero;
                
                _progressBar = progressBarObj.AddComponent<Image>();
                _progressBar.color = new Color(0.4f, 0.8f, 1f);
                _progressBar.type = Image.Type.Filled;
                _progressBar.fillMethod = Image.FillMethod.Horizontal;
                _progressBar.fillOrigin = 0;
                _progressBar.fillAmount = 0;

                DontDestroyOnLoad(canvasObj);
                
                Debug.Log("[ItemSourceViewer] 进度UI已创建");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ItemSourceViewer] 创建进度UI失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新进度显示
        /// </summary>
        void UpdateProgress(int current, int total, string sceneName)
        {
            if (_progressText != null && _progressBar != null)
            {
                float progress = total > 0 ? (float)current / total : 0f;
                _progressBar.fillAmount = progress;
                
                if (LocalizationManager.CurrentLanguage == SystemLanguage.ChineseSimplified ||
                    LocalizationManager.CurrentLanguage == SystemLanguage.ChineseTraditional)
                {
                    _progressText.text = $"正在分析: {sceneName}\n进度: {current}/{total} ({progress * 100:F1}%)";
                }
                else
                {
                    _progressText.text = $"Analyzing: {sceneName}\nProgress: {current}/{total} ({progress * 100:F1}%)";
                }
                
                Debug.Log($"[ItemSourceViewer] 分析进度: {current}/{total} - {sceneName}");
            }
        }

        /// <summary>
        /// 隐藏进度UI
        /// </summary>
        void HideProgress()
        {
            if (_progressPanel != null)
            {
                var canvas = _progressPanel.GetComponentInParent<Canvas>();
                if (canvas != null)
                {
                    Destroy(canvas.gameObject);
                }
                _progressPanel = null;
                _progressText = null;
                _progressBar = null;
            }
        }
        

        // 修改模型
        void UpdateModel(GameObject viewerObject)
        {
            try
            {
                var visualChildren = new List<Transform>();
                foreach (Transform child in viewerObject.transform)
                {
                    if (child.name == "Visual")
                    {
                        visualChildren.Add(child);
                    }
                }

                if (visualChildren.Count == 2)
                {
                    Transform? activeVisual = null;
                    Transform? inactiveVisual = null;

                    foreach (var visual in visualChildren)
                    {
                        if (visual.gameObject.activeSelf)
                            activeVisual = visual;
                        else
                            inactiveVisual = visual;
                    }

                    if (activeVisual != null && inactiveVisual != null)
                    {
                        activeVisual.gameObject.SetActive(false);
                        inactiveVisual.gameObject.SetActive(true);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ItemSourceViewer] 修改模型失败: {ex.Message}");
            }
        }

        private Dictionary<int, List<string>> BuildSourceMap()
        {
            var map = new Dictionary<int, List<string>>();

            // 1. 分析制作系统
            AnalyzeCrafting(map);
            
            // 2. 分析商店系统
            AnalyzeShops(map);
            
            // 3. 分析任务奖励
            AnalyzeQuestRewards(map);
            
            // 4. 分析种植系统
            AnalyzeCrops(map);
            
            // 5. 分析任务生成物品
            AnalyzeQuestSpawns(map);

            return map;
        }

        // 打开商店界面时的回调
        void OnFadeGroupShowComplete(FadeGroup fadeGroup)
        {
            try
            {
                var stockShopView = fadeGroup.gameObject.GetComponent<StockShopView>();
                
                // 只在物品来源查看器时进行处理
                if (stockShopView.Target.MerchantID != ItemSourceMerchantID)
                    return;

                // 添加搜索框
                AddSearchBox(stockShopView);
                
                // 添加物品来源显示到详情面板（会自动处理购买按钮隐藏）
                AddItemSourceToDetails(stockShopView);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ItemSourceViewer] 错误: {e.Message}");
            }
        }

        // 禁用购买UI（每次选择时调用，不会影响其他售货机）
        void DisablePurchaseUI(StockShopView stockShopView)
        {
            try
            {
                // 使用协程延迟执行，确保在StockShopView更新UI后再隐藏
                StartCoroutine(DisablePurchaseUICoroutine(stockShopView));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ItemSourceViewer] 禁用购买UI失败: {e.Message}");
            }
        }

        // 禁用购买UI的协程
        IEnumerator DisablePurchaseUICoroutine(StockShopView stockShopView)
        {
            // 等待一帧，让StockShopView先更新UI
            yield return null;

            try
            {
                // 检查stockShopView是否为空
                if (stockShopView == null)
                    yield break;

                // 根据MerchantID决定是否显示购买UI
                bool shouldShowPurchaseUI = stockShopView.Target.MerchantID != ItemSourceMerchantID;

                // 显示/隐藏交互按钮（购买按钮）
                var interactionButtonField = typeof(StockShopView).GetField("interactionButton",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (interactionButtonField != null)
                {
                    var button = interactionButtonField.GetValue(stockShopView) as Button;
                    if (button != null)
                    {
                        button.gameObject.SetActive(shouldShowPurchaseUI);
                    }
                }

                // 显示/隐藏价格显示
                var priceDisplayField = typeof(StockShopView).GetField("priceDisplay",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (priceDisplayField != null)
                {
                    var priceDisplay = priceDisplayField.GetValue(stockShopView) as GameObject;
                    if (priceDisplay != null)
                    {
                        priceDisplay.SetActive(shouldShowPurchaseUI);
                    }
                }

                // 显示/隐藏锁定显示
                var lockDisplayField = typeof(StockShopView).GetField("lockDisplay",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (lockDisplayField != null)
                {
                    var lockDisplay = lockDisplayField.GetValue(stockShopView) as GameObject;
                    if (lockDisplay != null)
                    {
                        lockDisplay.SetActive(shouldShowPurchaseUI);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ItemSourceViewer] 禁用购买UI协程失败: {e.Message}");
            }
        }

        // 存储ItemDetailsDisplay的GameObject用于添加物品来源
        private GameObject? _itemSourceDisplayObject;

        // 添加物品来源到详情面板
        void AddItemSourceToDetails(StockShopView stockShopView)
        {
            try
            {
                // 获取ItemDetailsDisplay
                var detailsField = typeof(StockShopView).GetField("details",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (detailsField == null)
                {
                    Debug.LogWarning("[ItemSourceViewer] 未找到details字段");
                    return;
                }

                var itemDetailsDisplay = detailsField.GetValue(stockShopView) as Component;
                if (itemDetailsDisplay == null)
                {
                    Debug.LogWarning("[ItemSourceViewer] itemDetailsDisplay为null");
                    return;
                }

                // 监听选择变化
                stockShopView.onSelectionChanged -= OnStockShopSelectionChanged;
                stockShopView.onSelectionChanged += OnStockShopSelectionChanged;
                
                // 创建物品来源显示对象
                CreateItemSourceDisplay(itemDetailsDisplay);
                
                Debug.Log("[ItemSourceViewer] 已添加物品来源显示");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ItemSourceViewer] 添加物品来源显示失败: {e.Message}");
            }
        }

        // 创建物品来源显示UI
        void CreateItemSourceDisplay(Component itemDetailsDisplay)
        {
            try
            {
                // 通过反射获取propertiesParent字段
                var propertiesParentField = itemDetailsDisplay.GetType().GetField("propertiesParent",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (propertiesParentField == null)
                {
                    Debug.LogWarning("[ItemSourceViewer] 未找到propertiesParent字段");
                    return;
                }

                var propertiesParent = propertiesParentField.GetValue(itemDetailsDisplay) as RectTransform;
                if (propertiesParent == null)
                {
                    Debug.LogWarning("[ItemSourceViewer] propertiesParent为null");
                    return;
                }

                // 检查是否已存在
                if (_itemSourceDisplayObject != null)
                {
                    return;
                }

                // 创建物品来源显示对象
                _itemSourceDisplayObject = new GameObject("ItemSourceDisplay");
                _itemSourceDisplayObject.transform.SetParent(propertiesParent, false);

                // 添加RectTransform
                var rectTransform = _itemSourceDisplayObject.AddComponent<RectTransform>();
                rectTransform.anchorMin = new Vector2(0, 0);
                rectTransform.anchorMax = new Vector2(1, 1);
                rectTransform.pivot = new Vector2(0.5f, 0.5f);

                // 添加VerticalLayoutGroup
                var verticalLayout = _itemSourceDisplayObject.AddComponent<VerticalLayoutGroup>();
                verticalLayout.childAlignment = TextAnchor.UpperLeft;
                verticalLayout.childControlHeight = false;
                verticalLayout.childControlWidth = true;
                verticalLayout.childForceExpandHeight = false;
                verticalLayout.childForceExpandWidth = true;
                verticalLayout.spacing = 2;
                verticalLayout.padding = new RectOffset(10, 10, 10, 10);

                // 添加ContentSizeFitter来自动调整容器高度
                var contentSizeFitter = _itemSourceDisplayObject.AddComponent<ContentSizeFitter>();
                contentSizeFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                contentSizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                // 添加LayoutElement
                var layoutElement = _itemSourceDisplayObject.AddComponent<LayoutElement>();
                layoutElement.preferredHeight = -1;
                layoutElement.flexibleHeight = -1;

                // 添加背景
                var background = _itemSourceDisplayObject.AddComponent<Image>();
                background.color = new Color(0.2f, 0.3f, 0.4f, 0.3f);

                // 添加标题
                CreateSourceTitle(_itemSourceDisplayObject.transform);

                // 初始隐藏
                _itemSourceDisplayObject.SetActive(false);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ItemSourceViewer] 创建物品来源显示失败: {e.Message}");
            }
        }

        // 创建来源标题
        void CreateSourceTitle(Transform parent)
        {
            var titleObject = new GameObject("Title");
            titleObject.transform.SetParent(parent, false);

            // 设置RectTransform
            var rectTransform = titleObject.AddComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0, 0);
            rectTransform.anchorMax = new Vector2(1, 1);
            rectTransform.pivot = new Vector2(0.5f, 0.5f);
            rectTransform.sizeDelta = new Vector2(0, 0);

            var titleText = titleObject.AddComponent<TextMeshProUGUI>();
            titleText.fontSize = 18;
            titleText.fontStyle = FontStyles.Bold;
            titleText.alignment = TextAlignmentOptions.Left;
            titleText.enableWordWrapping = false;
            
            if (LocalizationManager.CurrentLanguage == SystemLanguage.ChineseSimplified ||
                LocalizationManager.CurrentLanguage == SystemLanguage.ChineseTraditional)
            {
                titleText.text = "▼ 物品获取方式";
            }
            else
            {
                titleText.text = "▼ Item Sources";
            }
            
            titleText.color = new Color(0.4f, 0.8f, 1f);

            // 添加ContentSizeFitter来自动调整高度
            var contentSizeFitter = titleObject.AddComponent<ContentSizeFitter>();
            contentSizeFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            contentSizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var layoutElement = titleObject.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = -1;
            layoutElement.flexibleHeight = -1;
        }

        // 当商店选择变化时
        void OnStockShopSelectionChanged()
        {
            try
            {
                var stockShopView = StockShopView.Instance;
                if (stockShopView == null)
                    return;

                // 检查是否是我们的查看器
                bool isOurViewer = stockShopView.Target.MerchantID == ItemSourceMerchantID;

                if (!isOurViewer)
                {
                    // 不是我们的查看器，隐藏物品来源显示
                    if (_itemSourceDisplayObject != null)
                        _itemSourceDisplayObject.SetActive(false);
                    return;
                }

                // 是我们的查看器，隐藏购买UI
                DisablePurchaseUI(stockShopView);

                var selection = stockShopView.GetSelection();
                if (selection == null)
                {
                    if (_itemSourceDisplayObject != null)
                        _itemSourceDisplayObject.SetActive(false);
                    return;
                }

                var item = selection.GetItem();
                if (item == null)
                {
                    if (_itemSourceDisplayObject != null)
                        _itemSourceDisplayObject.SetActive(false);
                    return;
                }

                // 更新物品来源显示
                UpdateItemSourceDisplay(item.TypeID);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ItemSourceViewer] 更新选择失败: {e.Message}");
            }
        }

        // 更新物品来源显示
        void UpdateItemSourceDisplay(int itemID)
        {
            try
            {
                if (_itemSourceDisplayObject == null)
                    return;

                // 清除旧的来源条目（保留标题）
                var children = new List<Transform>();
                foreach (Transform child in _itemSourceDisplayObject.transform)
                {
                    if (child.name != "Title")
                        children.Add(child);
                }
                foreach (var child in children)
                {
                    Destroy(child.gameObject);
                }

                // 添加新的来源条目
                if (_itemSourceMap.ContainsKey(itemID))
                {
                    var sources = _itemSourceMap[itemID];
                    foreach (var source in sources)
                    {
                        CreateSourceEntry(_itemSourceDisplayObject.transform, source);
                    }
                    _itemSourceDisplayObject.SetActive(true);
                }
                else
                {
                    // 无来源信息
                    string noSourceText;
                    if (LocalizationManager.CurrentLanguage == SystemLanguage.ChineseSimplified ||
                        LocalizationManager.CurrentLanguage == SystemLanguage.ChineseTraditional)
                    {
                        noSourceText = "无已知获取方式";
                    }
                    else
                    {
                        noSourceText = "No known source";
                    }
                    CreateSourceEntry(_itemSourceDisplayObject.transform, noSourceText);
                    _itemSourceDisplayObject.SetActive(true);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ItemSourceViewer] 更新物品来源显示失败: {e.Message}");
            }
        }

        // 创建单个来源条目
        void CreateSourceEntry(Transform parent, string sourceText)
        {
            var entryObject = new GameObject("SourceEntry");
            entryObject.transform.SetParent(parent, false);

            // 设置RectTransform
            var rectTransform = entryObject.AddComponent<RectTransform>();
            rectTransform.anchorMin = new Vector2(0, 0);
            rectTransform.anchorMax = new Vector2(1, 1);
            rectTransform.pivot = new Vector2(0.5f, 0.5f);
            rectTransform.sizeDelta = new Vector2(0, 0);

            var text = entryObject.AddComponent<TextMeshProUGUI>();
            text.fontSize = 14;
            text.alignment = TextAlignmentOptions.Left;
            text.enableWordWrapping = true;
            text.text = "• " + sourceText;
            text.color = Color.white;

            // 添加ContentSizeFitter来自动调整高度
            var contentSizeFitter = entryObject.AddComponent<ContentSizeFitter>();
            contentSizeFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            contentSizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var layoutElement = entryObject.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = -1;
            layoutElement.flexibleHeight = -1;
        }

        // 添加搜索框
        void AddSearchBox(StockShopView stockShopView)
        {
            try
            {
                var merchantStuff = stockShopView.transform.Find("Content/MerchantStuff/Content");
                if (merchantStuff == null)
                    return;

                // 检查是否已存在
                var existingSearchBox = merchantStuff.Find("SearchBox");
                if (existingSearchBox != null)
                {
                    var tmpInputField = existingSearchBox.GetComponent<TMP_InputField>();
                    tmpInputField.text = string.Empty;
                    existingSearchBox.gameObject.SetActive(true);
                    return;
                }

                // 创建搜索框（与SuperPerkShop相同的实现）
                GameObject searchBox = new GameObject("SearchBox");
                searchBox.transform.SetParent(merchantStuff, false);
                searchBox.AddComponent<CanvasRenderer>();

                RectTransform rectTransform = searchBox.AddComponent<RectTransform>();
                rectTransform.anchorMin = new Vector2(0, 1);
                rectTransform.anchorMax = new Vector2(1, 1);
                rectTransform.pivot = new Vector2(0.5f, 1);
                rectTransform.offsetMin = new Vector2(10, -60);
                rectTransform.offsetMax = new Vector2(-10, 0);

                var layoutElement = searchBox.AddComponent<LayoutElement>();
                layoutElement.minHeight = 60;
                layoutElement.preferredHeight = 60;
                layoutElement.flexibleHeight = 0;
                layoutElement.flexibleWidth = 1;

                var background = searchBox.AddComponent<Image>();
                background.color = new Color(0.1f, 0.1f, 0.1f, 0.8f);

                var inputField = searchBox.AddComponent<TMP_InputField>();
                inputField.interactable = true;
                inputField.targetGraphic = background;

                // 文本区域
                GameObject textArea = new GameObject("Text Area");
                textArea.transform.SetParent(searchBox.transform, false);
                RectTransform textAreaRect = textArea.AddComponent<RectTransform>();
                textAreaRect.anchorMin = Vector2.zero;
                textAreaRect.anchorMax = Vector2.one;
                textAreaRect.offsetMin = new Vector2(10, 10);
                textAreaRect.offsetMax = new Vector2(-10, -10);
                textArea.AddComponent<RectMask2D>();

                GameObject textObject = new GameObject("Text");
                textObject.transform.SetParent(textArea.transform, false);
                var textComponent = textObject.AddComponent<TextMeshProUGUI>();
                textComponent.text = "";
                textComponent.alignment = TextAlignmentOptions.Left;

                GameObject placeholderObject = new GameObject("Placeholder");
                placeholderObject.transform.SetParent(textArea.transform, false);
                var placeholderText = placeholderObject.AddComponent<TextMeshProUGUI>();
                
                // 多语言支持
                if (LocalizationManager.CurrentLanguage == SystemLanguage.ChineseSimplified ||
                    LocalizationManager.CurrentLanguage == SystemLanguage.ChineseTraditional)
                {
                    placeholderText.text = "搜索物品...";
                }
                else
                {
                    placeholderText.text = "Search Item...";
                }
                
                placeholderText.alignment = TextAlignmentOptions.Left;
                placeholderText.fontStyle = FontStyles.Italic;
                placeholderText.color = new Color(1, 1, 1, 0.5f);

                inputField.textViewport = textAreaRect;
                inputField.textComponent = textComponent;
                inputField.placeholder = placeholderText;

                searchBox.transform.SetAsFirstSibling();

                // 添加搜索事件
                inputField.onValueChanged.AddListener((keyword) =>
                {
                    RefreshItemShow(stockShopView, keyword);
                });

                var eventTrigger = searchBox.AddComponent<EventTrigger>();
                var entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
                entry.callback.AddListener((data) =>
                {
                    inputField.Select();
                    inputField.ActivateInputField();
                });
                eventTrigger.triggers.Add(entry);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ItemSourceViewer] 添加搜索框失败: {e.Message}");
            }
        }

        // 刷新物品显示（搜索过滤）
        void RefreshItemShow(StockShopView stockShopView, string keyword)
        {
            try
            {
                var entryPoolProperty = typeof(StockShopView).GetProperty("EntryPool",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var entryPoolValue = entryPoolProperty?.GetValue(stockShopView);
                var prefabPool = entryPoolValue as PrefabPool<StockShopItemEntry>;
                
                if (prefabPool == null)
                    return;

                keyword = keyword.Trim().ToLower();

                foreach (var entry in prefabPool.ActiveEntries)
                {
                    if (string.IsNullOrEmpty(keyword))
                    {
                        entry.gameObject.SetActive(true);
                        continue;
                    }

                    var item = entry.GetItem();
                    if (item.DisplayName.ToLower().Contains(keyword) || 
                        item.TypeID.ToString() == keyword)
                    {
                        entry.gameObject.SetActive(true);
                    }
                    else
                    {
                        entry.gameObject.SetActive(false);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ItemSourceViewer] 刷新物品显示失败: {e.Message}");
            }
        }

        private void AnalyzeCrafting(Dictionary<int, List<string>> map)
        {
            var formulas = GameplayDataSettings.CraftingFormulas;
            if (formulas == null)
                return;

            foreach (var formula in formulas.Entries)
            {
                if (!formula.IDValid)
                    continue;

                int itemID = formula.result.id;
                string unlock = formula.unlockByDefault ? "默认解锁" : "需解锁";
                string perk = string.IsNullOrEmpty(formula.requirePerk) ? "" : $" [需技能:{formula.requirePerk}]";
                
                AddSource(map, itemID, $"[制作] 配方:{formula.id} | 产量:{formula.result.amount} | {unlock}{perk}");
            }
        }

        private void AnalyzeShops(Dictionary<int, List<string>> map)
        {
            var shopDB = GameplayDataSettings.StockshopDatabase;
            if (shopDB == null)
                return;

            foreach (var merchant in shopDB.merchantProfiles)
            {
                // 跳过我们自己的查看器
                if (merchant.merchantID == ItemSourceMerchantID)
                    continue;

                foreach (var entry in merchant.entries)
                {
                    string prob = entry.possibility < 1f ? $" 概率:{entry.possibility:P0}" : "";
                    AddSource(map, entry.typeID, 
                        $"[商店] 商人:{merchant.merchantID} | 库存:{entry.maxStock} | 价格倍率:{entry.priceFactor:F2}x{prob}");
                }
            }
        }

        private void AnalyzeQuestRewards(Dictionary<int, List<string>> map)
        {
            var questCollection = GameplayDataSettings.QuestCollection;
            if (questCollection == null)
                return;

            foreach (var quest in questCollection)
            {
                if (quest == null)
                    continue;

                foreach (var reward in quest.Rewards)
                {
                    if (reward is RewardItem itemReward)
                    {
                        AddSource(map, itemReward.itemTypeID, 
                            $"[任务奖励] 任务:{quest.DisplayName} (ID:{quest.ID}) | 数量:{itemReward.amount}");
                    }
                }
            }
        }

        private void AnalyzeCrops(Dictionary<int, List<string>> map)
        {
            var cropDB = GameplayDataSettings.CropDatabase;
            if (cropDB == null)
                return;

            try
            {
                var field = typeof(CropDatabase).GetField("crops", 
                    BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (field != null)
                {
                    var crops = field.GetValue(cropDB) as IEnumerable<CropInfo>;
                    if (crops != null)
                    {
                        foreach (var crop in crops)
                        {
                            for (int ranking = 0; ranking < 3; ranking++)
                            {
                                int productID = crop.GetProduct((ProductRanking)ranking);
                                if (productID > 0)
                                {
                                    AddSource(map, productID, 
                                        $"[种植] 作物:{crop.DisplayName} | 品质:{(ProductRanking)ranking} | 产量:{crop.resultAmount}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ItemSourceViewer] 分析种植系统时出错: {e.Message}");
            }
        }

        private void AnalyzeQuestSpawns(Dictionary<int, List<string>> map)
        {
            var questCollection = GameplayDataSettings.QuestCollection;
            if (questCollection == null)
                return;

            try
            {
                foreach (var quest in questCollection)
                {
                    if (quest == null)
                        continue;

                    var spawners = quest.GetComponentsInChildren<SpawnItemForTask>(true);
                    foreach (var spawner in spawners)
                    {
                        var field = typeof(SpawnItemForTask).GetField("itemID", 
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        
                        if (field != null)
                        {
                            int itemID = (int)field.GetValue(spawner);
                            if (itemID >= 0)
                            {
                                AddSource(map, itemID, 
                                    $"[任务生成] 任务:{quest.DisplayName} (ID:{quest.ID}) | 专属生成");
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ItemSourceViewer] 分析任务生成时出错: {e.Message}");
            }
        }


        private void AddSource(Dictionary<int, List<string>> map, int itemID, string source)
        {
            if (!map.ContainsKey(itemID))
            {
                map[itemID] = new List<string>();
            }
            map[itemID].Add(source);
        }
    }
}
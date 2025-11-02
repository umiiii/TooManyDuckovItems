using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Duckov.Utilities;
using ItemStatsSystem;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TooManyDuckovItems
{
    /// <summary>
    /// 场景物品来源分析器 - 通过加载场景提取宝箱和生成器配置
    /// </summary>
    public class ItemSourceAnalyzer
    {
        /// <summary>
        /// 分析进度回调 (当前进度, 总数, 场景名称)
        /// </summary>
        public Action<int, int, string> OnProgress;
        
        /// <summary>
        /// 分析完成回调 (找到的物品来源数量)
        /// </summary>
        public Action<int> OnComplete;

        private Dictionary<int, List<string>> _itemSourceMap;
        private HashSet<int> _analyzedScenes = new HashSet<int>();

        public ItemSourceAnalyzer(Dictionary<int, List<string>> sourceMap)
        {
            _itemSourceMap = sourceMap;
        }

        /// <summary>
        /// 异步分析所有场景中的宝箱和生成器
        /// </summary>
        public async UniTask AnalyzeAllScenesAsync()
        {
            try
            {
                Debug.Log("[ItemSourceAnalyzer] 开始分析所有场景...");

                // 获取场景列表
                var sceneEntries = SceneInfoCollection.Entries;
                if (sceneEntries == null || sceneEntries.Count == 0)
                {
                    Debug.LogWarning("[ItemSourceAnalyzer] 未找到场景列表");
                    OnComplete?.Invoke(0);
                    return;
                }

                // 过滤出有效场景（排除当前基础场景）
                var validScenes = sceneEntries
                    .Where(entry => entry != null && 
                           entry.ID != "Base" &&  // 使用ID而不是SceneReference.Name
                           entry.BuildIndex >= 0 &&
                           !_analyzedScenes.Contains(entry.BuildIndex))
                    .ToList();

                Debug.Log($"[ItemSourceAnalyzer] 找到 {validScenes.Count} 个待分析场景");

                int totalCount = validScenes.Count;
                int currentIndex = 0;
                int totalItemsFound = 0;

                foreach (var sceneEntry in validScenes)
                {
                    currentIndex++;
                    string sceneName = sceneEntry.DisplayName ?? sceneEntry.ID ?? "未知场景";
                    
                    OnProgress?.Invoke(currentIndex, totalCount, sceneName);
                    
                    try
                    {
                        int itemsInScene = await AnalyzeSceneAsync(sceneEntry, sceneName);
                        totalItemsFound += itemsInScene;
                        _analyzedScenes.Add(sceneEntry.BuildIndex);
                        
                        Debug.Log($"[ItemSourceAnalyzer] 场景 {sceneName} 分析完成，找到 {itemsInScene} 个物品来源");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[ItemSourceAnalyzer] 分析场景 {sceneName} 时出错: {ex.Message}\n{ex.StackTrace}");
                    }

                    // 每个场景之间等待较长时间，确保完全卸载
                    await UniTask.Delay(500); // 增加到500ms
                    
                    // 额外等待一帧，确保垃圾回收
                    await UniTask.Yield();
                }

                Debug.Log($"[ItemSourceAnalyzer] 所有场景分析完成！总计找到 {totalItemsFound} 个物品来源");
                OnComplete?.Invoke(totalItemsFound);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ItemSourceAnalyzer] 分析过程出错: {ex.Message}\n{ex.StackTrace}");
                OnComplete?.Invoke(0);
            }
        }

        /// <summary>
        /// 分析单个场景
        /// </summary>
        private async UniTask<int> AnalyzeSceneAsync(SceneInfoEntry sceneEntry, string sceneName)
        {
            int itemsFound = 0;
            Scene loadedScene = default;
            bool sceneLoaded = false;

            try
            {
                int buildIndex = sceneEntry.BuildIndex;
                
                Debug.Log($"[ItemSourceAnalyzer] 开始加载场景: {sceneName} (BuildIndex: {buildIndex})");

                // 异步加载场景（Additive模式，低优先级）
                var asyncOp = SceneManager.LoadSceneAsync(buildIndex, LoadSceneMode.Additive);
                if (asyncOp == null)
                {
                    Debug.LogWarning($"[ItemSourceAnalyzer] 无法加载场景 {sceneName}");
                    return 0;
                }

                // 设置为低优先级，避免影响游戏性能
                asyncOp.priority = 0; // 最低优先级
                asyncOp.allowSceneActivation = true;
                
                // 等待加载完成
                await UniTask.WaitUntil(() => asyncOp.isDone);
                
                // 加载完成后等待额外的时间，让场景初始化完成
                await UniTask.Delay(200);
                
                loadedScene = SceneManager.GetSceneByBuildIndex(buildIndex);
                if (!loadedScene.isLoaded)
                {
                    Debug.LogWarning($"[ItemSourceAnalyzer] 场景 {sceneName} 加载失败");
                    return 0;
                }

                sceneLoaded = true;
                Debug.Log($"[ItemSourceAnalyzer] 场景 {sceneName} 加载成功");

                // 分析场景中的组件
                itemsFound = AnalyzeSceneComponents(loadedScene, sceneName);

                // 等待一帧确保分析完成
                await UniTask.Yield();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ItemSourceAnalyzer] 分析场景 {sceneName} 内部错误: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                // 卸载场景
                if (sceneLoaded && loadedScene.isLoaded)
                {
                    try
                    {
                        Debug.Log($"[ItemSourceAnalyzer] 开始卸载场景: {sceneName}");
                        var unloadOp = SceneManager.UnloadSceneAsync(loadedScene);
                        if (unloadOp != null)
                        {
                            unloadOp.priority = 0; // 低优先级卸载
                            await UniTask.WaitUntil(() => unloadOp.isDone);
                            
                            // 等待GC清理
                            await UniTask.Delay(100);
                        }
                        Debug.Log($"[ItemSourceAnalyzer] 场景 {sceneName} 已卸载");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[ItemSourceAnalyzer] 卸载场景 {sceneName} 失败: {ex.Message}");
                    }
                }
            }

            return itemsFound;
        }

        /// <summary>
        /// 分析场景中的组件
        /// </summary>
        private int AnalyzeSceneComponents(Scene scene, string sceneName)
        {
            int itemsFound = 0;

            try
            {
                // 获取场景中所有根对象
                var rootObjects = scene.GetRootGameObjects();
                Debug.Log($"[ItemSourceAnalyzer] 场景 {sceneName} 有 {rootObjects.Length} 个根对象");

                // 查找所有 LootBoxLoader
                var allLootBoxes = new List<LootBoxLoader>();
                foreach (var rootObj in rootObjects)
                {
                    allLootBoxes.AddRange(rootObj.GetComponentsInChildren<LootBoxLoader>(true));
                }
                
                Debug.Log($"[ItemSourceAnalyzer] 场景 {sceneName} 找到 {allLootBoxes.Count} 个 LootBoxLoader");
                itemsFound += AnalyzeLootBoxes(allLootBoxes, sceneName);

                // 查找所有 LootSpawner
                var allLootSpawners = new List<LootSpawner>();
                foreach (var rootObj in rootObjects)
                {
                    allLootSpawners.AddRange(rootObj.GetComponentsInChildren<LootSpawner>(true));
                }
                
                Debug.Log($"[ItemSourceAnalyzer] 场景 {sceneName} 找到 {allLootSpawners.Count} 个 LootSpawner");
                itemsFound += AnalyzeLootSpawners(allLootSpawners, sceneName);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ItemSourceAnalyzer] 分析场景组件失败: {ex.Message}\n{ex.StackTrace}");
            }

            return itemsFound;
        }

        /// <summary>
        /// 分析 LootBoxLoader 列表
        /// </summary>
        private int AnalyzeLootBoxes(List<LootBoxLoader> lootBoxes, string sceneName)
        {
            int itemsFound = 0;

            foreach (var lootBox in lootBoxes)
            {
                try
                {
                    string location = GetLootBoxLocation(lootBox, sceneName);

                    // 1. 分析固定物品
                    var fixedItemsProperty = typeof(LootBoxLoader).GetProperty("FixedItems",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (fixedItemsProperty != null)
                    {
                        var fixedItems = fixedItemsProperty.GetValue(lootBox) as List<int>;
                        if (fixedItems != null && fixedItems.Count > 0)
                        {
                            foreach (var itemID in fixedItems)
                            {
                                if (itemID > 0)
                                {
                                    AddSource(itemID, $"[宝箱固定] {location}");
                                    itemsFound++;
                                }
                            }
                        }
                    }

                    // 2. 分析随机池
                    var randomFromPoolField = typeof(LootBoxLoader).GetField("randomFromPool",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (randomFromPoolField != null)
                    {
                        bool randomFromPool = (bool)randomFromPoolField.GetValue(lootBox);

                        if (randomFromPool)
                        {
                            var randomPoolField = typeof(LootBoxLoader).GetField("randomPool",
                                BindingFlags.NonPublic | BindingFlags.Instance);
                            if (randomPoolField != null)
                            {
                                var randomPool = randomPoolField.GetValue(lootBox);
                                var poolItems = ExtractRandomPoolItems(randomPool);
                                
                                foreach (var itemID in poolItems)
                                {
                                    AddSource(itemID, $"[宝箱随机池] {location}");
                                    itemsFound++;
                                }
                            }
                        }
                        else
                        {
                            // 通过标签和品质过滤（记录可能性）
                            itemsFound += AnalyzeLootBoxTags(lootBox, location);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ItemSourceAnalyzer] 分析单个宝箱失败: {ex.Message}");
                }
            }

            return itemsFound;
        }

        /// <summary>
        /// 分析 LootSpawner 列表
        /// </summary>
        private int AnalyzeLootSpawners(List<LootSpawner> spawners, string sceneName)
        {
            int itemsFound = 0;

            foreach (var spawner in spawners)
            {
                try
                {
                    string location = GetSpawnerLocation(spawner, sceneName);

                    // 检查随机生成模式
                    var randomGenrateField = typeof(LootSpawner).GetField("randomGenrate",
                        BindingFlags.Public | BindingFlags.Instance);
                    bool randomGenrate = true;
                    if (randomGenrateField != null)
                    {
                        randomGenrate = (bool)randomGenrateField.GetValue(spawner);
                    }

                    if (!randomGenrate)
                    {
                        // 固定物品模式
                        var fixedItemsField = typeof(LootSpawner).GetField("fixedItems",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        if (fixedItemsField != null)
                        {
                            var fixedItems = fixedItemsField.GetValue(spawner) as List<int>;
                            if (fixedItems != null)
                            {
                                foreach (var itemID in fixedItems)
                                {
                                    if (itemID > 0)
                                    {
                                        AddSource(itemID, $"[场景固定生成] {location}");
                                        itemsFound++;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // 随机生成模式
                        var randomFromPoolField = typeof(LootSpawner).GetField("randomFromPool",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (randomFromPoolField != null)
                        {
                            bool randomFromPool = (bool)randomFromPoolField.GetValue(spawner);

                            if (randomFromPool)
                            {
                                var randomPoolField = typeof(LootSpawner).GetField("randomPool",
                                    BindingFlags.NonPublic | BindingFlags.Instance);
                                if (randomPoolField != null)
                                {
                                    var randomPool = randomPoolField.GetValue(spawner);
                                    var poolItems = ExtractRandomPoolItems(randomPool);
                                    
                                    foreach (var itemID in poolItems)
                                    {
                                        AddSource(itemID, $"[场景随机池] {location}");
                                        itemsFound++;
                                    }
                                }
                            }
                            else
                            {
                                // 标签过滤模式
                                itemsFound += AnalyzeSpawnerTags(spawner, location);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ItemSourceAnalyzer] 分析单个生成器失败: {ex.Message}");
                }
            }

            return itemsFound;
        }

        /// <summary>
        /// 从随机池中提取物品ID列表
        /// </summary>
        private List<int> ExtractRandomPoolItems(object randomPool)
        {
            var result = new List<int>();
            
            try
            {
                var entriesField = randomPool.GetType().GetField("entries",
                    BindingFlags.Public | BindingFlags.Instance);
                    
                if (entriesField != null)
                {
                    var entries = entriesField.GetValue(randomPool);
                    if (entries is System.Collections.IEnumerable enumerable)
                    {
                        foreach (var entry in enumerable)
                        {
                            var valueField = entry.GetType().GetField("value",
                                BindingFlags.Public | BindingFlags.Instance);
                                
                            if (valueField != null)
                            {
                                var value = valueField.GetValue(entry);
                                if (value != null)
                                {
                                    var itemTypeIDField = value.GetType().GetField("itemTypeID",
                                        BindingFlags.Public | BindingFlags.Instance);
                                        
                                    if (itemTypeIDField != null)
                                    {
                                        int itemID = (int)itemTypeIDField.GetValue(value);
                                        if (itemID > 0 && !result.Contains(itemID))
                                        {
                                            result.Add(itemID);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ItemSourceAnalyzer] 提取随机池失败: {ex.Message}");
            }
            
            return result;
        }

        /// <summary>
        /// 分析宝箱的标签配置（记录所有可能掉落的物品）
        /// </summary>
        private int AnalyzeLootBoxTags(LootBoxLoader lootBox, string location)
        {
            int itemsFound = 0;

            try
            {
                var tagsField = typeof(LootBoxLoader).GetField("tags",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var qualitiesField = typeof(LootBoxLoader).GetField("qualities",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var excludeTagsField = typeof(LootBoxLoader).GetField("excludeTags",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (tagsField == null || qualitiesField == null) return 0;

                var tagsContainer = tagsField.GetValue(lootBox);
                var qualitiesContainer = qualitiesField.GetValue(lootBox);

                var tags = ExtractRandomContainerValues<Tag>(tagsContainer);
                var qualities = ExtractRandomContainerValues<int>(qualitiesContainer);
                
                List<Tag> excludeTags = new List<Tag>();
                if (excludeTagsField != null)
                {
                    var excludeList = excludeTagsField.GetValue(lootBox) as List<Tag>;
                    if (excludeList != null)
                        excludeTags = excludeList;
                }

                // 为每个标签+品质组合查找物品
                foreach (var tag in tags)
                {
                    foreach (var quality in qualities)
                    {
                        var matchingItems = ItemAssetsCollection.Search(new ItemFilter
                        {
                            requireTags = new Tag[] { tag },
                            excludeTags = excludeTags.ToArray(),
                            minQuality = quality,
                            maxQuality = quality
                        });

                        foreach (var itemID in matchingItems)
                        {
                            AddSource(itemID, $"[宝箱标签掉落] {location} | 标签:{tag.name} 品质:{quality}");
                            itemsFound++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ItemSourceAnalyzer] 分析宝箱标签失败: {ex.Message}");
            }

            return itemsFound;
        }

        /// <summary>
        /// 分析生成器的标签配置
        /// </summary>
        private int AnalyzeSpawnerTags(LootSpawner spawner, string location)
        {
            int itemsFound = 0;

            try
            {
                var tagsField = typeof(LootSpawner).GetField("tags",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var qualitiesField = typeof(LootSpawner).GetField("qualities",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var excludeTagsField = typeof(LootSpawner).GetField("excludeTags",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (tagsField == null || qualitiesField == null) return 0;

                var tagsContainer = tagsField.GetValue(spawner);
                var qualitiesContainer = qualitiesField.GetValue(spawner);

                var tags = ExtractRandomContainerValues<Tag>(tagsContainer);
                var qualities = ExtractRandomContainerValues<int>(qualitiesContainer);
                
                List<Tag> excludeTags = new List<Tag>();
                if (excludeTagsField != null)
                {
                    var excludeList = excludeTagsField.GetValue(spawner) as List<Tag>;
                    if (excludeList != null)
                        excludeTags = excludeList;
                }

                // 为每个标签+品质组合查找物品
                foreach (var tag in tags)
                {
                    foreach (var quality in qualities)
                    {
                        var matchingItems = ItemAssetsCollection.Search(new ItemFilter
                        {
                            requireTags = new Tag[] { tag },
                            excludeTags = excludeTags.ToArray(),
                            minQuality = quality,
                            maxQuality = quality
                        });

                        foreach (var itemID in matchingItems)
                        {
                            AddSource(itemID, $"[场景标签生成] {location} | 标签:{tag.name} 品质:{quality}");
                            itemsFound++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ItemSourceAnalyzer] 分析生成器标签失败: {ex.Message}");
            }

            return itemsFound;
        }

        /// <summary>
        /// 从RandomContainer中提取值列表
        /// </summary>
        private List<T> ExtractRandomContainerValues<T>(object container)
        {
            var result = new List<T>();
            
            try
            {
                var entriesField = container.GetType().GetField("entries",
                    BindingFlags.Public | BindingFlags.Instance);
                    
                if (entriesField != null)
                {
                    var entries = entriesField.GetValue(container);
                    if (entries is System.Collections.IEnumerable enumerable)
                    {
                        foreach (var entry in enumerable)
                        {
                            var valueField = entry.GetType().GetField("value",
                                BindingFlags.Public | BindingFlags.Instance);
                                
                            if (valueField != null)
                            {
                                T value = (T)valueField.GetValue(entry);
                                if (value != null && !result.Contains(value))
                                {
                                    result.Add(value);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ItemSourceAnalyzer] 提取RandomContainer值失败: {ex.Message}");
            }
            
            return result;
        }

        /// <summary>
        /// 获取宝箱位置描述
        /// </summary>
        private string GetLootBoxLocation(LootBoxLoader lootBox, string sceneName)
        {
            var pos = lootBox.transform.position;
            var path = GetGameObjectPath(lootBox.gameObject);
            return $"{sceneName} | {path} | ({pos.x:F1}, {pos.y:F1}, {pos.z:F1})";
        }

        /// <summary>
        /// 获取生成器位置描述
        /// </summary>
        private string GetSpawnerLocation(LootSpawner spawner, string sceneName)
        {
            var pos = spawner.transform.position;
            var path = GetGameObjectPath(spawner.gameObject);
            return $"{sceneName} | {path} | ({pos.x:F1}, {pos.y:F1}, {pos.z:F1})";
        }

        /// <summary>
        /// 获取GameObject层级路径
        /// </summary>
        private string GetGameObjectPath(GameObject obj)
        {
            if (obj == null) return "Unknown";
            
            string path = obj.name;
            Transform current = obj.transform.parent;
            int depth = 0;
            
            while (current != null && depth < 3) // 只显示3层父级
            {
                path = current.name + "/" + path;
                current = current.parent;
                depth++;
            }
            
            return path;
        }

        /// <summary>
        /// 添加物品来源
        /// </summary>
        private void AddSource(int itemID, string source)
        {
            if (!_itemSourceMap.ContainsKey(itemID))
            {
                _itemSourceMap[itemID] = new List<string>();
            }
            
            if (!_itemSourceMap[itemID].Contains(source))
            {
                _itemSourceMap[itemID].Add(source);
            }
        }
    }
}


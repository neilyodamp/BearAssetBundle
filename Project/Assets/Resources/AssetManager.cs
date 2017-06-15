using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

//这个主要针对Resource 加载出来的资源做引用计数
[Serializable]
public class AssetReference
{
    public Object _asset;
    public int _referenceCount;
    public string _path; //资源加载路径
    public int _unReferenceFrameCount;//处于无引用状态多少帧了
    public bool _autoClear = true;
    public int _autoClearFrame = 100; //无引用后多少帧内删除掉

    //AB的一些的信息记录
    public bool _isLoadByAssetBundle = false;
    public string _assetbundlePath = null;
    public string _assetName = null;

    public AssetReference(string path, Object asset)
    {
        this._asset = asset;
        this._path = path;
        this._referenceCount = 0;
        this._isLoadByAssetBundle = AssetManager.Instance.TryGetAssetBundlePath(path, out _assetbundlePath, out _assetName);
    }

    public void Reference()
    {
        _referenceCount++;
        _unReferenceFrameCount = 0;
    }

    //提供一个逻辑层，针对某个资源控制是否自动释放的机会
    //具体可以由LoadTask传入、上层直接AssetManager通过Path来设定
    public bool ShouldAutoClear(bool immediate)
    {
        if (!_autoClear)
        {
            return false;
        }

        if (immediate) return true;

        return _unReferenceFrameCount >= _autoClearFrame;
    }

    public void IncreaseUnReferenceFrame()
    {
        _unReferenceFrameCount ++;
    }

    public void UnReference()
    {
        _referenceCount--;
    }

    public bool HasReference()
    {
        return _referenceCount > 0;
    }

}

//资源管理器
//资源的加载、释放，缓存
//AssetManager管理的是加载后的资源，已经加载请求，某个资源是否在loading，则由Loader负责
//各个Loader只负责加载，不负责缓存，所有的缓存放到这一级，集中管理
//Loader可能自己也需要做引用计数（如AssetBundleLoader），则在自己内部做
//把Loader功能单一化了，能不做缓存、计数，就不做
public class AssetManager : MonoBehaviour
{
    public static AssetManager Instance;

    //记录所有的加载的资源，做引用计数，辅助自动释放
    private Dictionary<int, AssetReference> _loadedAssets;
    //记录所有的已加载的资源，但是用path作为key
    private Dictionary<string, AssetReference> _loadedAssetsByPath;
    //减GC的临时数据结构
    private List<AssetReference> _unusedAssets = new List<AssetReference>();
    private HashSet<string> _unusedABs = new HashSet<string>();

    private ILoadingQueue _loadingQueue;

    private int _unusedResLoadCount = 0;
    private Coroutine _loadCoroutine;
    private Coroutine _clearCoroutine;

    //始终持有的资源
    private HashSet<string> _keepedAssets;

    public static void Create()
    {
        GameObject go = new GameObject("AssetManager");
        go.AddComponent<AssetManager>();
        GameObject.DontDestroyOnLoad(go);
    }

    void Awake()
    {
        //建立一个异步loading队列
        _loadingQueue = new AsyncLoadingQueue();
        _loadingQueue.SetRequestLoaded(OnRequestLoaded);
        _loadingQueue.SetTaskLoader(LoadTask);
        _loadingQueue.SetTaskAsyncLoader(LoadTaskAsync);
        _loadedAssets = new Dictionary<int, AssetReference>();
        _loadedAssetsByPath = new Dictionary<string, AssetReference>();
        Instance = this;
    }

    void OnDestroy()
    {
        Instance = null;
    }

    public IEnumerator UnloadUnused()
    {
        yield return Resources.UnloadUnusedAssets();
        yield return ClearUnusedAsset(true);
    }

    public IEnumerator Init()
    {
        ResourcesLoader.Create();
        yield return null;
        //ABLoader.Create();
        //yield return ABLoader.Ins.Init();
        StartLoad();
        yield return null;
        StartAutoClear();
    }

    //启动加载
    public void StartLoad()
    {
        StopLoad();
        _loadCoroutine = StartCoroutine(_DoLoad());
    }

    //停止加载
    public void StopLoad()
    {
        if (_loadCoroutine != null)
        {
            StopCoroutine(_loadCoroutine);
            _loadCoroutine = null;
        }
    }

    //开始清理
    public void StartAutoClear()
    {
        StopAutoClear();
        _clearCoroutine = StartCoroutine(_DoClear());
    }

    //停止清理
    public void StopAutoClear()
    {
        if (_clearCoroutine != null)
        {
            StopCoroutine(_clearCoroutine);
            _clearCoroutine = null;
        }
    }

    // 一直检测该队列是不是有 加载请求
    private IEnumerator _DoLoad()
    {
        while (true)
        {
            yield return _loadingQueue.Load();
        }
    }

    //一直检测是否有资源是否失去了引用
    private IEnumerator _DoClear()
    {
        while (true)
        {
            yield return ClearUnusedAsset(false);
        }
    }

    //提交一个 同步加载请求
    public void LoadRequest(LoadRequest req)
    {
        if (req == null)
        {
            return;
        }

        foreach (var task in req)
        {
            LoadTask(task);
        }

        OnRequestLoaded(req);
    }

    //提交一个 异步加载请求
    public void LoadRequestAsync(LoadRequest req,LoadRequest.OnAllLoaded onLoaded = null)
    {
        if (req == null)
        {
            return;
        }
        //辅助写法,防止回调设置晚了
        if (req.onAllLoaded == null && onLoaded != null)
        {
            req.onAllLoaded = onLoaded;
        }

        //Debug.Log("New Request");
        _loadingQueue.AddLoadRequest(req);
    }

    //请求执行完后的回调
    private void OnRequestLoaded(LoadRequest req)
    {
        foreach (var task in req)
        {
            AssetReference assetRef;
            if (TryGetAssetReference(task.LoadedAsset, out assetRef))
            {
                assetRef.UnReference(); //释放Request带来的引用
            }
        }
        if (!req.IsCancel)
        {
            req.CallAllLoaded();
        }  
    }

    //将引用计数托管给GameObject
    public void ReferenceAssetsWithGo(GameObject go, LoadRequest request)
    {
        if (request == null || go == null)
        {
            return;
        }
        AssetReferenceComponent comp = TryGetAssetReferenceComponent(go);
        foreach (var task in request)
        {
            if (task.LoadedAsset != null)
            {
                ReferenceAssetWithGo(comp,task.LoadedAsset);
            }
        }
    }

    public void ReferenceAssetsWithGo(GameObject go, List<Object> assets)
    {
        if (assets == null || go == null)
        {
            return;
        }
        AssetReferenceComponent comp = TryGetAssetReferenceComponent(go);
        foreach (var asset in assets)
        {
            if (asset != null)
            {
                ReferenceAssetWithGo(comp, asset);
            }
        }
    }

    //对GameObject增加一个资源引用，GameObject被destroy的时候，会自动释放引用
    public void ReferenceAssetWithGo(GameObject go, Object asset)
    {
        if (go == null)
        {
            return;
        }
        AssetReferenceComponent comp = TryGetAssetReferenceComponent(go);
        ReferenceAssetWithGo(comp,asset);
    }

    
    public void ReferenceAssetWithGo(AssetReferenceComponent comp, Object asset)
    {
        if (asset == null)
            return;
        if (comp != null && comp.AddReference(asset))
        {
            //引用计数 +1
            ReferenceAsset(asset);
        }
    }

    private AssetReferenceComponent TryGetAssetReferenceComponent(GameObject go)
    {
        if (go == null)
        {
            return null;
        }
        AssetReferenceComponent comp = go.GetComponent<AssetReferenceComponent>();
        if (comp == null)
        {
            comp = go.AddComponent<AssetReferenceComponent>();
        }
        return comp;
    }

    //解掉这个资源的asset 引用
    public void UnReferenceAssetWithGo(GameObject go, Object asset)
    {
        if (go == null)
        {
            return;
        }
        AssetReferenceComponent comp = go.GetComponent<AssetReferenceComponent>();
        if (comp == null)
        {
            return;
        }
        if (comp.RemoveRef(asset))
        {
            UnReferenceAsset(asset);
        }
    }

    //手动引用一个Asset，这个Asset不会被自动释放掉
    public void ReferenceAsset(Object asset)
    {
        if (asset == null)
        {
            return;
        }
        AssetReference assetRef = null;

        //这个资源不并在归 AssetManager 管理
        if (!TryGetAssetReference(asset, out assetRef))
        {
            Debug.LogWarning("asset not managed:" + asset);
            return;
        }
        //
        assetRef.Reference();

        //AB需要记录AB包本身的引用,对包内资源的引用，都算对AB包的一次引用
        if (assetRef._isLoadByAssetBundle)
        {
            //TODO,LRU，调整ab包的优先级
            AssetBundleLoader.Instance.ReferenceAssetBundle(assetRef._assetbundlePath);
        }
    }

    //释放这个手动引用的Asset，这个Asset引用为0时可能被自动释放
    public void UnReferenceAsset(Object asset)
    {
        if (asset == null)
        {
            return;
        }
        AssetReference assetRef = null;
        if (!TryGetAssetReference(asset, out assetRef))
        {
            return;
        }
        assetRef.UnReference();

        if (assetRef._isLoadByAssetBundle)
        {
            AssetBundleLoader.Instance.UnReferenceAssetBundle(assetRef._assetbundlePath);
        }
    }
 
    //
    private IEnumerator ClearUnusedAsset(bool immediate)
    {
        if (_loadedAssets.Count <= 0)
        {
            yield break;
        }

        //清空列表
        _unusedAssets.Clear();

        //查找 已加载的资源 那些失去了引用.
        foreach (KeyValuePair<int, AssetReference> pair in _loadedAssets)
        {
            AssetReference assetRef = pair.Value;
            if (!assetRef.HasReference())
            {
                if (assetRef.ShouldAutoClear(immediate) && !IsAlwaysKeep(assetRef._path))
                {
                    _unusedAssets.Add(assetRef);
                }
                else
                {
                    assetRef.IncreaseUnReferenceFrame();
                }
            }
        }

        //可以设置一个最大数
        //或者某些资源的最大数
        if (_unusedAssets.Count <= 0)
        {
            yield break;
        }

        
        foreach (var assetRef in _unusedAssets)
        {
            RemoveLoaded(assetRef);

            if (assetRef._isLoadByAssetBundle)
            {
                //如果是AB，去重复，然后Unload掉 ,这里是记录一下这个资源是来自于哪个AB的.
                _unusedABs.Add(assetRef._assetbundlePath);
            }
            else
            {
                //Resources，记录一下无用的次数
                //这个计数其实不准确的，只是大概反应一下无用的资源数量
                //Resources到底要不要执行unloadunused，还得具体看实际情况
                _unusedResLoadCount++;
            }
        }
        _unusedAssets.Clear();

        if (AssetBundleLoader.Instance != null)
        {
            foreach (var abPath in _unusedABs)
            {
                //AB引用为0，并且没在加载中，那Unload掉
                if (!AssetBundleLoader.Instance.HasReference(abPath) && !AssetBundleLoader.Instance.IsLoading(abPath))
                {
                    //TODO,增加LRU缓存，超过最大数，才触发删除,把这个set修改成LRU就可以了
                    AssetBundleLoader.Instance.UnloadAssetBundle(abPath);
                }
            }
        }

        
        _unusedABs.Clear();

        yield return null;

        //超过阈值，执行UnloadUnused
        if (_unusedResLoadCount >= 10)
        {
            _unusedResLoadCount = 0;
            yield return Resources.UnloadUnusedAssets();
            Debug.Log("AssetManager:UnloadUnused Called");
        }
    }

    public bool TryGetAssetReference(Object asset, out AssetReference assetRef)
    {
        if (asset == null)
        {
            assetRef = null;
            return false;
        }
        return _loadedAssets.TryGetValue(asset.GetInstanceID(), out assetRef);
    }

    public int GetReferenceCount(Object asset)
    {
        AssetReference assetRef = null;
        if (!TryGetAssetReference(asset, out assetRef))
        {
            return 0;
        }
        return assetRef._referenceCount;
    }

    public bool HasReference(Object asset)
    {
        return GetReferenceCount(asset) > 0;
    }

    #region 对一些 对资源 是自动清理机制 还是 手动清理机制
    //加载是不是 始终持有的 资源
    public bool IsAlwaysKeep(string path)
    {
        if (_keepedAssets == null) return false;
        return _keepedAssets.Contains(path);
    }

    //表明这个资源 是 始终持有的
    public void DontAutoClear(string path)
    {
        AssetReference assetRef;
        if (_loadedAssetsByPath.TryGetValue(path, out assetRef))
        {
            assetRef._autoClear = false;
        }
        AddKeepAssets(path);
    }

    //
    public void DontAutoClear(Object asset)
    {
        AssetReference assetRef;
        if (TryGetAssetReference(asset, out assetRef))
        {
            assetRef._autoClear = false;
            AddKeepAssets(assetRef._path);
        }
    }

    public void EnableClear(string path)
    {
        AssetReference assetRef;
        if (_loadedAssetsByPath.TryGetValue(path, out assetRef))
        {
            assetRef._autoClear = true;
        }
        RemoveKeepAssets(path);
    }

    public void EnableClear(Object asset)
    {
        AssetReference assetRef;
        if (TryGetAssetReference(asset, out assetRef))
        {
            assetRef._autoClear = true;
            RemoveKeepAssets(assetRef._path);
        }
    }

    public void AddKeepAssets(string path)
    {
        if (_keepedAssets == null)
        {
            _keepedAssets = new HashSet<string>();
        }
        _keepedAssets.Add(path);
    }

    public void RemoveKeepAssets(string path)
    {
        if (_keepedAssets != null)
        {
            _keepedAssets.Remove(path);
        }
    }

    #endregion

    //不用随意调用这个接口，删除的操作，统一在Clear里做
    private void RemoveLoaded(AssetReference assetRef)
    {
        //Debug.Log("AssetMgr:Auto Clear,"+assetRef.path);
        _loadedAssets.Remove(assetRef._asset.GetInstanceID());
        _loadedAssetsByPath.Remove(assetRef._path);
    }
    
    //同步加载资源
    public void LoadTask(LoadTask task)
    {
        if (task.IsDone)
            return;

        Object asset = Load(task.Path);
        task.IsDone = true;
        task.LoadedAsset = asset;
        NewLoaded(task.Path,asset);
    }


    //异步加载资源
    public void LoadTaskAsync(LoadTask task)
    {
        if (task.IsDone)
            return;

        LoadAsync(task.Path, (obj) =>
        {
            task.IsDone = true;
            task.LoadedAsset = obj;
            NewLoaded(task.Path, obj);
        });
    }

    //同步
    public Object Load(string path)
    {
        AssetReference assetRef;
        if (_loadedAssetsByPath.TryGetValue(path, out assetRef))
        {
            return assetRef._asset;
        }

        string abPath, assetName;

        
        if (!TryGetAssetBundlePath(path, out abPath, out assetName))
        {
            //该路径为 ResourceLoader
            if (ResourcesLoader.Instance == null)
            {
                Debug.LogWarning("ResLoader not init yet! Load failed");
                return null;
            }
            return ResourcesLoader.Instance.Load(path);
        }
        else
        {
            if (AssetBundleLoader.Instance == null)
            {
                Debug.LogWarning("ABLoader not init yet! Load failed");
                return null;
            }
            return AssetBundleLoader.Instance.LoadFromAssetBundle(abPath, assetName);
        }
    }

    //异步
    public Coroutine LoadAsync(string path, Action<Object> onLoaded)
    {
        AssetReference assetRef;
        if (_loadedAssetsByPath.TryGetValue(path, out assetRef))
        {
            if (onLoaded != null)
            {
                onLoaded(assetRef._asset);
            }
            return null;
        }

        //AB，Resource都是用异步加载，每次都开一个协程  == AsyncLoadingQueue
        //这样就是所有请求会一帧执行，最终有可能全部提交Unity去加载
        //当然最终Unity底层加载可能真是线程异步加载，但是这一桢还是执行了别的操作
        //Resource版本只是简单的提交，AB版本会执行一些必要的查找和依赖检查

        //还有方案是：
        //1.开一个协程，然后使用AB和Resource都是用同步加载,AssetManager层面模拟异步（任务数量，加载耗时） == LoadingQueue
        //2.开一个协程，然后AB和Resource的用异步加载，一次提交一个加载，加载完一个继续提交 == AsyncLoadingQueueSeq
        //3.开一个协程，AB,Rescource用异步加载，AB的管理器用同步处理提交，一次性全部提交  未实现.
        //3这个方案比较困难，AB因为有依赖的问题，同步提交、异步加载实现起来比较困难

        string abPath, assetName;
        if (!TryGetAssetBundlePath(path, out abPath, out assetName))
        {
            if (ResourcesLoader.Instance == null)
            {
                Debug.LogWarning("ResLoader not init yet! Load failed");
                return null;
            }
            //coroutine也可以保存起来，如果有需求可以全部停止,或者选择性停止
            return StartCoroutine(ResourcesLoader.Instance.LoadAsync(path, onLoaded));
        }
        else
        {
            if (AssetBundleLoader.Instance == null)
            {
                Debug.LogWarning("ABLoader not init yet! Load failed");
                return null;
            }
            return StartCoroutine(AssetBundleLoader.Instance.LoadFromAssetBundleAsync(abPath, assetName, onLoaded));
            //协程也可以由具体的loader发起，现在统一在AssetMgr层发起，统一管理
            //AssetBundleLoader.instance.StartLoadFromAssetBundle(abPath, assetName, (obj) =>
            //{
            //task.IsDone = true;
            //task.LoadedAsset = obj;
            //NewLoaded(task.Path, obj);
            //});
        }
    }

    //添加一个引用计数规则.
    private void NewLoaded(string assetPath, Object asset)
    {
        if (asset == null)
        {
            Debug.LogWarning("load asset failed:" + assetPath);
            return;
        }

        AssetReference assetRef;

        if (TryGetAssetReference(asset, out assetRef))
        {
            assetRef.Reference(); //新加载的？引用一下，有个任务在持有它
            return;
        }
        AssetReference newVal = new AssetReference(assetPath, asset);
        _loadedAssets[asset.GetInstanceID()] = newVal;
        _loadedAssetsByPath[assetPath] = newVal;
        newVal.Reference(); //新加载的？引用一下，有个任务在持有它
    }


    public bool TryGetAssetBundlePath(string path, out string abPath, out string asssetName)
    {
        abPath = null;
        asssetName = null;
        return false;
    }

    public void DumpInfo()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("AssetManager Info:");
        foreach (var pair in _loadedAssets)
        {
            AssetReference assetRef = pair.Value;
            sb.AppendFormat("Asset:{0},Reference Count:{1}\n", assetRef._path, assetRef._referenceCount);
        }
        sb.AppendLine("AssetManager Info Done.");
        Debug.Log(sb.ToString());
    }

    //辅助component，记录所有关联的asset
    public class AssetReferenceComponent : MonoBehaviour
    {
        private HashSet<Object> _references = new HashSet<Object>();

        void OnDestroy()
        {
            foreach (var asset in _references)
            {
                if (AssetManager.Instance != null)
                {
                    AssetManager.Instance.UnReferenceAsset(asset);
                }
            }
            _references.Clear();
        }

        public bool AddReference(Object asset)
        {
            return _references.Add(asset);
        }

        public bool RemoveRef(Object asset)
        {
            return _references.Remove(asset);
        }
    }
}

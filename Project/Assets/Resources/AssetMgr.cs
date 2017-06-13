using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;


[Serializable]
public class AssetRef
{
    public Object asset;
    public int refCount;
    public string path; //资源加载路径
    public int unRefFrameCount;//处于无引用状态多少帧了
    public bool autoClear = true;
    public int autoClearFrame = 100; //无引用后多少帧内删除掉

    //AB的一些的信息记录
    public bool isLoadByAB = false;
    public string abPath = null;
    public string assetName = null;

    public AssetRef(string path, Object asset)
    {
        this.asset = asset;
        this.path = path;
        this.refCount = 0;
        this.isLoadByAB = AssetMgr.Ins.TryGetABPath(path, out abPath, out assetName);
    }

    public void Ref()
    {
        refCount++;
        unRefFrameCount = 0;
    }

    //提供一个逻辑层，针对某个资源控制是否自动释放的机会
    //具体可以由LoadTask传入、上层直接AssetMgr通过Path来设定
    public bool ShouldAutoClear(bool immediate)
    {
        if (!autoClear)
        {
            return false;
        }

        if (immediate) return true;

        return unRefFrameCount >= autoClearFrame;
    }

    public void IncUnRefFrame()
    {
        unRefFrameCount ++;
    }

    public void UnRef()
    {
        refCount--;
    }

    public bool HasRef()
    {
        return refCount > 0;
    }
}



//资源管理器
//资源的加载、释放，缓存
//AssetMgr管理的是加载后的资源，已经加载请求，某个资源是否在loading，则由Loader负责
//各个Loader只负责加载，不负责缓存，所有的缓存放到这一级，集中管理
//Loader可能自己也需要做引用计数（如ABLoader），则在自己内部做
//把Loader功能单一化了，能不做缓存、计数，就不做
public class AssetMgr : MonoBehaviour
{
    public static AssetMgr Ins;

    //记录所有的加载的资源，做引用计数，辅助自动释放
    private Dictionary<int, AssetRef> mLoadedAssets;
    //记录所有的已加载的资源，但是用path作为key
    private Dictionary<string, AssetRef> mLoadedAssetsByPath;
    //减GC的临时数据结构
    private List<AssetRef> mUnusedAssets = new List<AssetRef>();
    private HashSet<string> mUnusedABs = new HashSet<string>();

    private ILoadingQueue q;

    private int mUnusedResLoadCount = 0;
    private Coroutine mLoadCoroutine;
    private Coroutine mClearCoroutine;

    //始终持有的资源
    private HashSet<string> mKeepedAssets;

    public static void Create()
    {
        GameObject go = new GameObject("AssetMgr");
        go.AddComponent<AssetMgr>();
        GameObject.DontDestroyOnLoad(go);
    }

    void Awake()
    {
        q = new AsyncLoadingQueue();
        q.SetRequestLoaded(OnRequestLoaded);
        q.SetTaskLoader(LoadTask);
        q.SetTaskAsyncLoader(LoadTaskAsync);
        mLoadedAssets = new Dictionary<int, AssetRef>();
        mLoadedAssetsByPath = new Dictionary<string, AssetRef>();
        Ins = this;
    }

    void OnDestroy()
    {
        Ins = null;
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

    public void StartLoad()
    {
        StopLoad();
        mLoadCoroutine = StartCoroutine(doLoad());
    }

    public void StopLoad()
    {
        if (mLoadCoroutine != null)
        {
            StopCoroutine(mLoadCoroutine);
            mLoadCoroutine = null;
        }
    }

    public void StartAutoClear()
    {
        StopAutoClear();
        mClearCoroutine = StartCoroutine(doClear());
    }

    public void StopAutoClear()
    {
        if (mClearCoroutine != null)
        {
            StopCoroutine(mClearCoroutine);
            mClearCoroutine = null;
        }
    }

    private IEnumerator doLoad()
    {
        while (true)
        {
            yield return q.Load();
        }
    }

    private IEnumerator doClear()
    {
        while (true)
        {
            yield return ClearUnusedAsset(false);
        }
    }

    //同步加载
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

    //异步队列加载
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
        q.AddLoadRequest(req);
    }

    private void OnRequestLoaded(LoadRequest req)
    {
        foreach (var task in req)
        {
            AssetRef assetRef;
            if (TryGetAssetRef(task.LoadedAsset, out assetRef))
            {
                assetRef.UnRef(); //释放Request带来的引用
            }
        }
        if (!req.IsCancel)
        {
            req.CallAllLoaded();
        }  
    }

    public void RefAssetsWithGo(GameObject go, LoadRequest request)
    {
        if (request == null || go == null)
        {
            return;
        }
        AssetRefComp comp = TryGetAssetRefComp(go);
        foreach (var task in request)
        {
            if (task.LoadedAsset != null)
            {
                RefAssetWithGo(comp,task.LoadedAsset);
            }
        }
    }

    public void RefAssetsWithGo(GameObject go, List<Object> assets)
    {
        if (assets == null || go == null)
        {
            return;
        }
        AssetRefComp comp = TryGetAssetRefComp(go);
        foreach (var asset in assets)
        {
            if (asset != null)
            {
                RefAssetWithGo(comp, asset);
            }
        }
    }

    //对GameObject增加一个资源引用，GameObject被destroy的时候，会自动释放引用
    public void RefAssetWithGo(GameObject go, Object asset)
    {
        if (go == null)
        {
            return;
        }
        AssetRefComp comp = TryGetAssetRefComp(go);
        RefAssetWithGo(comp,asset);
    }

    public void RefAssetWithGo(AssetRefComp comp, Object asset)
    {
        if (asset == null)
            return;
        if (comp != null && comp.AddRef(asset))
        {
            RefAsset(asset);
        }
    }

    private AssetRefComp TryGetAssetRefComp(GameObject go)
    {
        if (go == null)
        {
            return null;
        }
        AssetRefComp comp = go.GetComponent<AssetRefComp>();
        if (comp == null)
        {
            comp = go.AddComponent<AssetRefComp>();
        }
        return comp;
    }

    public void UnRefAssetWithGo(GameObject go, Object asset)
    {
        if (go == null)
        {
            return;
        }
        AssetRefComp comp = go.GetComponent<AssetRefComp>();
        if (comp == null)
        {
            return;
        }
        if (comp.RemoveRef(asset))
        {
            UnRefAsset(asset);
        }
    }

    //手动引用一个Asset，这个Asset不会被自动释放掉
    public void RefAsset(Object asset)
    {
        if (asset == null)
        {
            return;
        }
        AssetRef assetRef = null;
        if (!TryGetAssetRef(asset, out assetRef))
        {
            Debug.LogWarning("asset not managed:" + asset);
            return;
        }
        assetRef.Ref();

        //AB需要记录AB包本身的引用,对包内资源的引用，都算对AB包的一次引用
        if (assetRef.isLoadByAB)
        {
            //TODO,LRU，调整ab包的优先级
            AssetBundleLoader.instance.ReferenceAssetBundle(assetRef.abPath);
        }
    }

    //释放这个手动引用的Asset，这个Asset引用为0时可能被自动释放
    public void UnRefAsset(Object asset)
    {
        if (asset == null)
        {
            return;
        }
        AssetRef assetRef = null;
        if (!TryGetAssetRef(asset, out assetRef))
        {
            return;
        }
        assetRef.UnRef();

        if (assetRef.isLoadByAB)
        {
            AssetBundleLoader.instance.UnReferenceAssetBundle(assetRef.abPath);
        }
    }
 
    private IEnumerator ClearUnusedAsset(bool immediate)
    {
        if (mLoadedAssets.Count <= 0)
        {
            yield break;
        }

        mUnusedAssets.Clear();
        foreach (KeyValuePair<int, AssetRef> pkv in mLoadedAssets)
        {
            AssetRef assetRef = pkv.Value;
            if (!assetRef.HasRef())
            {
                if (assetRef.ShouldAutoClear(immediate) && !IsAlwaysKeep(assetRef.path))
                {
                    mUnusedAssets.Add(assetRef);
                }
                else
                {
                    assetRef.IncUnRefFrame();
                }
            }
        }

        //可以设置一个最大数
        //或者某些资源的最大数
        if (mUnusedAssets.Count <= 0)
        {
            yield break;
        }

        foreach (var assetRef in mUnusedAssets)
        {
            RemoveLoaded(assetRef);

            if (assetRef.isLoadByAB)
            {
                //如果是AB，去重复，然后Unload掉
                mUnusedABs.Add(assetRef.abPath);
            }
            else
            {
                //Resources，记录一下无用的次数
                //这个计数其实不准确的，只是大概反应一下无用的资源数量
                //Resources到底要不要执行unloadunused，还得具体看实际情况
                mUnusedResLoadCount++;
            }
        }
        mUnusedAssets.Clear();

        if (AssetBundleLoader.instance != null)
        {
            foreach (var abPath in mUnusedABs)
            {
                //AB引用为0，并且没在加载中，那Unload掉
                if (!AssetBundleLoader.instance.HasReference(abPath) && !AssetBundleLoader.instance.IsLoading(abPath))
                {
                    //TODO,增加LRU缓存，超过最大数，才触发删除,把这个set修改成LRU就可以了
                    AssetBundleLoader.instance.UnloadAssetBundle(abPath);
                }
            }
        }
        mUnusedABs.Clear();

        yield return null;

        //超过阈值，执行UnloadUnused
        if (mUnusedResLoadCount >= 10)
        {
            mUnusedResLoadCount = 0;
            yield return Resources.UnloadUnusedAssets();
            Debug.Log("AssetMgr:UnloadUnused Called");
        }
    }

    public bool TryGetAssetRef(Object asset, out AssetRef assetRef)
    {
        if (asset == null)
        {
            assetRef = null;
            return false;
        }
        return mLoadedAssets.TryGetValue(asset.GetInstanceID(), out assetRef);
    }

    public int GetRefCount(Object asset)
    {
        AssetRef assetRef = null;
        if (!TryGetAssetRef(asset, out assetRef))
        {
            return 0;
        }
        return assetRef.refCount;
    }

    public bool HasRef(Object asset)
    {
        return GetRefCount(asset) > 0;
    }

    public bool IsAlwaysKeep(string path)
    {
        if (mKeepedAssets == null) return false;
        return mKeepedAssets.Contains(path);
    }

    public void DontAutoClear(string path)
    {
        AssetRef assetRef;
        if (mLoadedAssetsByPath.TryGetValue(path, out assetRef))
        {
            assetRef.autoClear = false;
        }
        AddKeepAssets(path);
    }

    public void DontAutoClear(Object asset)
    {
        AssetRef assetRef;
        if (TryGetAssetRef(asset, out assetRef))
        {
            assetRef.autoClear = false;
            AddKeepAssets(assetRef.path);
        }
    }

    public void EnableClear(string path)
    {
        AssetRef assetRef;
        if (mLoadedAssetsByPath.TryGetValue(path, out assetRef))
        {
            assetRef.autoClear = true;
        }
        RemoveKeepAssets(path);
    }

    public void EnableClear(Object asset)
    {
        AssetRef assetRef;
        if (TryGetAssetRef(asset, out assetRef))
        {
            assetRef.autoClear = true;
            RemoveKeepAssets(assetRef.path);
        }
    }

    public void AddKeepAssets(string path)
    {
        if (mKeepedAssets == null)
        {
            mKeepedAssets = new HashSet<string>();
        }
        mKeepedAssets.Add(path);
    }

    public void RemoveKeepAssets(string path)
    {
        if (mKeepedAssets != null)
        {
            mKeepedAssets.Remove(path);
        }
    }

    //不用随意调用这个接口，删错的操作，统一在Clear里做
    private void RemoveLoaded(AssetRef assetRef)
    {
        //Debug.Log("AssetMgr:Auto Clear,"+assetRef.path);
        mLoadedAssets.Remove(assetRef.asset.GetInstanceID());
        mLoadedAssetsByPath.Remove(assetRef.path);
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

    public Object Load(string path)
    {
        AssetRef assetRef;
        if (mLoadedAssetsByPath.TryGetValue(path, out assetRef))
        {
            return assetRef.asset;
        }

        string abPath, assetName;
        if (!TryGetABPath(path, out abPath, out assetName))
        {
            if (ResourcesLoader.Ins == null)
            {
                Debug.LogWarning("ResLoader not init yet! Load failed");
                return null;
            }
            return ResourcesLoader.Ins.Load(path);
        }
        else
        {
            if (AssetBundleLoader.instance == null)
            {
                Debug.LogWarning("ABLoader not init yet! Load failed");
                return null;
            }
            return AssetBundleLoader.instance.LoadFromAB(abPath, assetName);
        }
    }

    public Coroutine LoadAsync(string path, Action<Object> onLoaded)
    {
        AssetRef assetRef;
        if (mLoadedAssetsByPath.TryGetValue(path, out assetRef))
        {
            if (onLoaded != null)
            {
                onLoaded(assetRef.asset);
            }
            return null;
        }

        //AB，Resource都是用异步加载，每次都开一个协程
        //这样就是所有请求会一帧执行，最终有可能全部提交Unity去加载
        //当然最终Unity底层加载可能真是线程异步加载，但是这一桢还是执行了别的操作
        //Resource版本只是简单的提交，AB版本会执行一些必要的查找和依赖检查

        //还有方案是：
        //1.开一个协程，然后使用AB和Resource都是用同步加载,AssetMgr层面模拟异步（任务数量，加载耗时）
        //2.开一个协程，然后AB和Resource的用异步加载，一次提交一个加载，加载完一个继续提交
        //3.开一个协程，AB,Rescource用异步加载，AB的管理器用同步处理提交，一次性全部提交
        //3这个方案比较困难，AB因为有依赖的问题，同步提交、异步加载实现起来比较困难

        string abPath, assetName;
        if (!TryGetABPath(path, out abPath, out assetName))
        {
            if (ResourcesLoader.Ins == null)
            {
                Debug.LogWarning("ResLoader not init yet! Load failed");
                return null;
            }
            //coroutine也可以保存起来，如果有需求可以全部停止,或者选择性停止
            return StartCoroutine(ResourcesLoader.Ins.LoadAsync(path, onLoaded));
        }
        else
        {
            if (AssetBundleLoader.instance == null)
            {
                Debug.LogWarning("ABLoader not init yet! Load failed");
                return null;
            }
            return StartCoroutine(AssetBundleLoader.instance.LoadFromAssetBundleAsync(abPath, assetName, onLoaded));
            //协程也可以由具体的loader发起，现在统一在AssetMgr层发起，统一管理
            //ABLoader.Ins.StartLoadFromAB(abPath, assetName, (obj) =>
            //{
            //task.IsDone = true;
            //task.LoadedAsset = obj;
            //NewLoaded(task.Path, obj);
            //});
        }
    }


    private void NewLoaded(string assetPath, Object asset)
    {
        if (asset == null)
        {
            Debug.LogWarning("load asset failed:" + assetPath);
            return;
        }

        AssetRef assetRef;
        if (TryGetAssetRef(asset, out assetRef))
        {
            assetRef.Ref(); //新加载的？引用一下，有个任务在持有它
            return;
        }
        AssetRef newVal = new AssetRef(assetPath, asset);
        mLoadedAssets[asset.GetInstanceID()] = newVal;
        mLoadedAssetsByPath[assetPath] = newVal;
        newVal.Ref(); //新加载的？引用一下，有个任务在持有它
    }


    public bool TryGetABPath(string path, out string abPath, out string asssetName)
    {
        abPath = null;
        asssetName = null;
        return false;
    }

    public void DumpInfo()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("AssetMgr Info:");
        foreach (var pair in mLoadedAssets)
        {
            AssetRef assetRef = pair.Value;
            sb.AppendFormat("Asset:{0},RefCount:{1}\n", assetRef.path, assetRef.refCount);
        }
        sb.AppendLine("AssetMgr Info Done.");
        Debug.Log(sb.ToString());
    }

    //辅助component，记录所有关联的asset
    public class AssetRefComp : MonoBehaviour
    {
        private HashSet<Object> refs = new HashSet<Object>();

        void OnDestroy()
        {
            foreach (var asset in refs)
            {
                if (AssetMgr.Ins != null)
                {
                    AssetMgr.Ins.UnRefAsset(asset);
                }
            }
            refs.Clear();
        }

        public bool AddRef(Object asset)
        {
            return refs.Add(asset);
        }

        public bool RemoveRef(Object asset)
        {
            return refs.Remove(asset);
        }
    }
}

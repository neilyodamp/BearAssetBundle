using System.Collections;
using UnityEngine;
using System.Collections.Generic;
using System;
using System.IO;
using Object = UnityEngine.Object;

public class ABLoader : MonoBehaviour
{
    [Serializable]
    private class ABRef
    {
        public AssetBundle ab;
        public int refCount = 0;
        public string path;

        public int depCount = 0;

        public void Ref()
        {
            refCount++;
        }

        public void UnRef()
        {
            refCount--;
        }

        public void RefDep()
        {
            depCount++;
        }

        public void UnRefDep()
        {
            depCount--;
        }

        public bool HasDepRef()
        {
            return depCount > 0;
        }

        public bool HasRef()
        {
            return refCount > 0;
        }
    }

    private AssetBundleManifest mManifest;
    private Dictionary<string, ABRef> mLoadedAB;
    private HashSet<string> mLoadingABs;
    private HashSet<string> mLoadingAssets;

    public static ABLoader Ins;

    public static void Create()
    {
        GameObject go = new GameObject("ABLoader");
        Ins = go.AddComponent<ABLoader>();
        DontDestroyOnLoad(go);
    }

    public Coroutine StartInit()
    {
        return StartCoroutine(Init());
    }

    public void Awake()
    {
        mLoadedAB = new Dictionary<string, ABRef>();
        mLoadingABs = new HashSet<string>();
        mLoadingAssets = new HashSet<string>();
    }

    public void OnDestroy()
    {
        Ins = null;
    }

    public IEnumerator Init()
    {
        string manifestPath = "StreamingAssets";
        yield return LoadFromABAsync(manifestPath, "AssetBundleManifest", (asset) =>
        {
            if (asset == null)
            {
                Debug.Log("Init Failed,Load Manifest failed");
                return;
            }
            mManifest = asset as AssetBundleManifest;
        });
    }

    //加载一个AB，但是不加载AB里的资源
    public Coroutine StartLoadAB(string abPath, Action<AssetBundle> cb = null)
    {
        return StartCoroutine(_LoadABAsync(abPath, cb));
    }

    public IEnumerator LoadABAsync(string abPath, Action<AssetBundle> cb = null)
    {
        return _LoadABAsync(abPath, cb);
    }

    //卸载AB，完整释放掉AB的内存
    public void UnloadAB(string abPath,bool unloadDependencies = true)
    {
        bool isUnload = _UnloadAB(abPath,false);

        if (!isUnload)
        {
            return;
        }

        if (unloadDependencies)
        {
            string[] deps = mManifest.GetAllDependencies(abPath);
            foreach (string dep in deps)
            {
                _UnloadAB(dep,true);
            }
        }
    }

    public bool HasRef(string abPath)
    {
        ABRef abRef;
        if (!mLoadedAB.TryGetValue(abPath, out abRef))
        {
            return false;
        }
        return abRef.HasRef();
    }

    public bool IsLoading(string abPath)
    {
        return mLoadingABs.Contains(abPath);
    }

    //增加一个引用
    public void RefAB(string abPath)
    {
        if (String.IsNullOrEmpty(abPath))
        {
            return;
        }
        ABRef abRef;
        if (!mLoadedAB.TryGetValue(abPath, out abRef))
        {
            return;
        }
        abRef.Ref();
    }

    //减少一个引用
    public void UnRefAB(string abPath)
    {
        if (String.IsNullOrEmpty(abPath))
        {
            return;
        }
        ABRef abRef;
        if (!mLoadedAB.TryGetValue(abPath, out abRef))
        {
            return;
        }
        abRef.UnRef();
    }

    //从AB中加载资源，如果AB没被加载，则自动加载这个AB，然后再从AB中加载资源
    public Coroutine StartLoadFromAB(string abPath, string assetName,Action<UnityEngine.Object> cb = null)
    {
        return StartCoroutine(_LoadFromABAsync(abPath, assetName, cb));
    }

    public IEnumerator LoadFromABAsync(string abPath, string assetName, Action<UnityEngine.Object> cb = null)
    {
        return _LoadFromABAsync(abPath, assetName, cb);
    }

    private IEnumerator _LoadFromABAsync(string abPath, string assetName, Action<UnityEngine.Object> cb)
    {
        ABRef abRef;
        if (!mLoadedAB.TryGetValue(abPath, out abRef))
        {
            //AB没加载，发起加载
            yield return _LoadABAsync(abPath, null);
            if (!mLoadedAB.TryGetValue(abPath, out abRef))
            {
                //加载完后，加载失败
                if (cb != null)
                {
                    cb(null);
                }
                yield break;
            }
        }

        string key = string.Format("{0}@{1}", abPath, assetName);
        if (mLoadingAssets.Contains(key))
        {
            while (mLoadingAssets.Contains(key))
            {
                yield return null;
            }
            //因为不记录所有加载过的asset，所有调用同步接口获取一下
            //记录了加载过的AB，因为有引用计数的需求
            //但是asset就没用引用计数的需求,asset计数了，也不能释放
            //因此不做这个记录，能省一些事，维护记录容易出bug
            UnityEngine.Object asset = abRef.ab.LoadAsset(key); //已经加载过了，同步加载会直接返回
            if (cb != null)
            {
                cb(asset);
            }
            yield break;
        }

        mLoadingAssets.Add(key);
        AssetBundleRequest abReq = abRef.ab.LoadAssetAsync(assetName);
        yield return abReq;
        mLoadingAssets.Remove(key);
        if (abReq.asset == null)
        {
            Debug.LogWarningFormat("No found {0} in {1}", assetName, abPath);
            yield break;
        }
        if (cb != null)
        {
            cb(abReq.asset);
        }
    }

    public Object LoadFromAB(string abPath, string assetName)
    {
        ABRef abRef;
        AssetBundle ab;
        if (!mLoadedAB.TryGetValue(abPath, out abRef))
        {
            //AB没加载，发起加载
            ab = LoadAB(abPath);
            //加载AB失败
            if (ab == null)
            {
                return null;
            }
        }
        else
        {
            ab = abRef.ab;
        }
        Object asset = ab.LoadAsset(assetName);
        if (asset == null)
        {
            Debug.LogWarningFormat("No found {0} in {1}", assetName, abPath);
            return null;
        }
        return asset;
    }

    //尝试加载一个AB
    //加载过了直接返回，没有加载过，会执行异步加载
    //如果加载的是依赖，会增加依赖计数
    private IEnumerator _LoadABInternalAsync(string abPath,bool isDep)
    {
        ABRef abRef;
        if (mLoadedAB.TryGetValue(abPath, out abRef))
        {
            //已经加载了，持有一个依赖引用
            if (isDep)
            {
                abRef.RefDep();
            }
            yield break;
        }

        //如果在加载队列里
        if (mLoadingABs.Contains(abPath))
        {
            //等待加载完成
            while (mLoadingABs.Contains(abPath))
            {
                yield return null;
            }
            //加载完成了，但是可能加载失败，获取一次
            if (isDep)
            {
                if (mLoadedAB.TryGetValue(abPath, out abRef))
                {
                    //持有一个依赖引用
                    abRef.RefDep();
                }
            }
            yield break;
        }

        mLoadingABs.Add(abPath);
        AssetBundleCreateRequest request = AssetBundle.LoadFromFileAsync(GetFullPath(abPath));
        yield return request;

        if (request.assetBundle == null)
        {
            if (mLoadingABs.Contains(abPath))
                mLoadingABs.Remove(abPath);
            yield break;
        }

        abRef = new ABRef();
        abRef.ab = request.assetBundle;
        abRef.path = abPath;
        mLoadedAB.Add(abPath, abRef);
        if (mLoadingABs.Contains(abPath))
        {
            //如果还在加载队列里，则记录依赖引用；不在加载队列里，说明提交底层加载后，AB却被Unload了
            if (isDep)
            {
                abRef.RefDep();
            }
            mLoadingABs.Remove(abPath);
        }
    }

    private void _LoadABInternal(string abPath, bool isDep)
    {
        ABRef abRef;
        if (mLoadedAB.TryGetValue(abPath, out abRef))
        {
            //已经加载了，持有一个依赖引用
            if (isDep)
            {
                abRef.RefDep();
            }
            return;
        }

        AssetBundle ab = AssetBundle.LoadFromFile(GetFullPath(abPath));
        if (ab == null)
        {
            return;
        }
        abRef = new ABRef();
        abRef.ab = ab;
        abRef.path = abPath;
        mLoadedAB.Add(abPath, abRef);
        if (isDep)
        {
            abRef.RefDep();
        }
    }

    private IEnumerator _LoadABAsync(string abPath, Action<AssetBundle> cb)
    {
        //尝试加载，不会产出依赖计数
        yield return _LoadABInternalAsync(abPath,false);

        //尝试加载依赖，所有的加载会产生依赖计数
        string[] deps = mManifest.GetAllDependencies(abPath);
        foreach (string dep in deps)
        {
            //一个加载完后，再加载下一个
            yield return _LoadABInternalAsync(dep, true);
        }

        if (cb != null)
        {
            ABRef abRef;
            if (!mLoadedAB.TryGetValue(abPath, out abRef))
            {
                cb(null);
                yield break;
            }
            cb(abRef.ab);
        }
    }

    public AssetBundle LoadAB(string abPath)
    {
        //尝试加载，不会产出依赖计数
        _LoadABInternal(abPath,false);

        string[] deps = mManifest.GetAllDependencies(abPath);
        foreach (string dep in deps)
        {
            _LoadABInternal(dep, true);
        }

        ABRef abRef;
        if (!mLoadedAB.TryGetValue(abPath, out abRef))
        {
            return null;
        }
        return abRef.ab;
    }

    private bool _UnloadAB(string abPath,bool isDep)
    {
        if (mLoadingABs.Contains(abPath))
        {
            //还在加载，删除，并返回
            mLoadingABs.Remove(abPath);
            return true;
        }

        ABRef abRef;
        if (!mLoadedAB.TryGetValue(abPath, out abRef))
        {
            return false;
        }

        if (isDep)
        {
            abRef.UnRefDep(); //释放依赖
        }

        if (abRef.HasDepRef()) //被依赖
        {
            return false;
        }

        if (abRef.HasRef()) //被引用
        {
            return false;
        }

        abRef.ab.Unload(true);
        abRef.ab = null;
        mLoadedAB.Remove(abPath);
        return true;
    }


    public string GetFullPath(string abPath)
    {
        return Path.Combine(Application.streamingAssetsPath, abPath);
    }

}

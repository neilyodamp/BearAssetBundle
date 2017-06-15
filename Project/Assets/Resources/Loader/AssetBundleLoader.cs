using System.Collections;
using UnityEngine;
using System.Collections.Generic;
using System;
using System.IO;
using Object = UnityEngine.Object;

public class AssetBundleLoader : MonoBehaviour
{
    [Serializable]
    private class AssetBundleReference
    {
        public AssetBundle _assetbundle;
        public int _referenceCount = 0;
        public string _path;

        public int _dependCount = 0;

        public void Reference()
        {
            _referenceCount++;
        }

        public void UnReference()
        {
            _referenceCount--;
        }

        public void Depend()
        {
            _dependCount++;
        }

        public void UnDepend()
        {
            _dependCount--;
        }

        public bool HasDepend()
        {
            return _dependCount > 0;
        }

        public bool HasReference()
        {
            return _referenceCount > 0;
        }
    }

    private AssetBundleManifest _manifest;
    private Dictionary<string, AssetBundleReference> _loadedAssetBundles;
    private HashSet<string> _loadingAssetBundles;
    private HashSet<string> _loadingAssets;

    public static AssetBundleLoader Instance;

    public static void Create()
    {
        GameObject go = new GameObject("ABLoader");
        Instance = go.AddComponent<AssetBundleLoader>();
        DontDestroyOnLoad(go);
    }

    public Coroutine StartInit()
    {
        return StartCoroutine(Init());
    }

    public void Awake()
    {
        _loadedAssetBundles = new Dictionary<string, AssetBundleReference>();
        _loadingAssetBundles = new HashSet<string>();
        _loadingAssets = new HashSet<string>();
    }

    public void OnDestroy()
    {
        Instance = null;
    }

    public IEnumerator Init()
    {
        string manifestPath = "StreamingAssets";
        yield return LoadFromAssetBundleAsync(manifestPath, "AssetBundleManifest", (asset) =>
        {
            if (asset == null)
            {
                Debug.Log("Init Failed,Load Manifest failed");
                return;
            }
            _manifest = asset as AssetBundleManifest;
        });
    }

    //加载一个AB，但是不加载AB里的资源
    public Coroutine StartLoadAssetBundle(string abPath, Action<AssetBundle> callback = null)
    {
        return StartCoroutine(_LoadAssetBundleAsync(abPath, callback));
    }

    public IEnumerator LoadAssetBundleAsync(string abPath, Action<AssetBundle> callback = null)
    {
        return _LoadAssetBundleAsync(abPath, callback);
    }

    //卸载AB，完整释放掉AB的内存
    public void UnloadAssetBundle(string abPath,bool unloadDependencies = true)
    {
        bool isUnload = _UnloadAssetBundle(abPath,false);

        if (!isUnload)
        {
            return;
        }

        if (unloadDependencies)
        {
            string[] deps = _manifest.GetAllDependencies(abPath);
            foreach (string dep in deps)
            {
                _UnloadAssetBundle(dep,true);
            }
        }
    }

    public bool HasReference(string abPath)
    {
        AssetBundleReference abRef;
        if (!_loadedAssetBundles.TryGetValue(abPath, out abRef))
        {
            return false;
        }
        return abRef.HasReference();
    }

    public bool IsLoading(string abPath)
    {
        return _loadingAssetBundles.Contains(abPath);
    }

    //增加一个引用
    public void ReferenceAssetBundle(string abPath)
    {
        if (String.IsNullOrEmpty(abPath))
        {
            return;
        }
        AssetBundleReference abRef;
        if (!_loadedAssetBundles.TryGetValue(abPath, out abRef))
        {
            return;
        }
        abRef.Reference();
    }

    //减少一个引用
    public void UnReferenceAssetBundle(string abPath)
    {
        if (String.IsNullOrEmpty(abPath))
        {
            return;
        }
        AssetBundleReference abRef;
        if (!_loadedAssetBundles.TryGetValue(abPath, out abRef))
        {
            return;
        }
        abRef.UnReference();
    }

    //从AB中加载资源，如果AB没被加载，则自动加载这个AB，然后再从AB中加载资源
    public Coroutine StartLoadFromAssetBundle(string abPath, string assetName,Action<UnityEngine.Object> callback = null)
    {
        return StartCoroutine(_LoadFromAssetBundleAsync(abPath, assetName, callback));
    }

    public IEnumerator LoadFromAssetBundleAsync(string abPath, string assetName, Action<UnityEngine.Object> callback = null)
    {
        return _LoadFromAssetBundleAsync(abPath, assetName, callback);
    }

    private IEnumerator _LoadFromAssetBundleAsync(string abPath, string assetName, Action<UnityEngine.Object> callback)
    {
        AssetBundleReference abRef;
        if (!_loadedAssetBundles.TryGetValue(abPath, out abRef))
        {
            //AB没加载，发起加载
            yield return _LoadAssetBundleAsync(abPath, null);
            if (!_loadedAssetBundles.TryGetValue(abPath, out abRef))
            {
                //加载完后，加载失败
                if (callback != null)
                {
                    callback(null);
                }
                yield break;
            }
        }

        string key = string.Format("{0}@{1}", abPath, assetName);
        
        //正在异步加载资源中
        if (_loadingAssets.Contains(key))
        {
            while (_loadingAssets.Contains(key))
            {
                yield return null;
            }
            //因为不记录所有加载过的asset，所有调用同步接口获取一下
            //记录了加载过的AB，因为有引用计数的需求
            //但是asset就没用引用计数的需求,asset计数了，也不能释放
            //因此不做这个记录，能省一些事，维护记录容易出bug
            UnityEngine.Object asset = abRef._assetbundle.LoadAsset(key); //已经加载过了，同步加载会直接返回
            if (callback != null)
            {
                callback(asset);
            }
            yield break;
        }

        //从未加载过该资源
        _loadingAssets.Add(key);
        AssetBundleRequest abReq = abRef._assetbundle.LoadAssetAsync(assetName);
        yield return abReq;
        _loadingAssets.Remove(key);
        if (abReq.asset == null)
        {
            Debug.LogWarningFormat("No found {0} in {1}", assetName, abPath);
            yield break;
        }
        if (callback != null)
        {
            callback(abReq.asset);
        }
    }

    //同步加载出资源
    public Object LoadFromAssetBundle(string abPath, string assetName)
    {
        AssetBundleReference abRef;
        AssetBundle ab;
        if (!_loadedAssetBundles.TryGetValue(abPath, out abRef))
        {
            //AB没加载，发起加载
            ab = LoadAssetBundle(abPath);
            //加载AB失败
            if (ab == null)
            {
                return null;
            }
        }
        else
        {
            ab = abRef._assetbundle;
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
    private IEnumerator _LoadAssetBundleInternalAsync(string abPath,bool isDep)
    {
        AssetBundleReference abRef;
        if (_loadedAssetBundles.TryGetValue(abPath, out abRef))
        {
            //已经加载了，持有一个依赖引用
            if (isDep)
            {
                abRef.Depend();
            }
            yield break;
        }

        //如果在加载队列里
        if (_loadingAssetBundles.Contains(abPath))
        {
            //等待加载完成
            while (_loadingAssetBundles.Contains(abPath))
            {
                yield return null;
            }
            //加载完成了，但是可能加载失败，获取一次
            if (isDep)
            {
                if (_loadedAssetBundles.TryGetValue(abPath, out abRef))
                {
                    //持有一个依赖引用
                    abRef.Depend();
                }
            }
            yield break;
        }

        _loadingAssetBundles.Add(abPath);
        
        AssetBundleCreateRequest request = AssetBundle.LoadFromFileAsync(GetFullPath(abPath));
        yield return request;

        //加载完成s
        if (request.assetBundle == null)
        {
            if (_loadingAssetBundles.Contains(abPath))
                _loadingAssetBundles.Remove(abPath);
            yield break;
        }

        abRef = new AssetBundleReference();
        abRef._assetbundle = request.assetBundle;
        abRef._path = abPath;
        _loadedAssetBundles.Add(abPath, abRef);

        //如果还在加载队列里，则记录依赖引用；不在加载队列里，说明提交底层加载后，AB却被Unload了
        if (_loadingAssetBundles.Contains(abPath))
        {
            if (isDep)
            {
                abRef.Depend(); 
            }
            _loadingAssetBundles.Remove(abPath);
        }
    }

    //
    private void _LoadAssetBundleInternal(string abPath, bool isDep)
    {
        AssetBundleReference abRef;
        if (_loadedAssetBundles.TryGetValue(abPath, out abRef))
        {
            //已经加载了，持有一个依赖引用
            if (isDep)
            {
                abRef.Depend();
            }
            return;
        }

        AssetBundle ab = AssetBundle.LoadFromFile(GetFullPath(abPath));
        if (ab == null)
        {
            return;
        }
        abRef = new AssetBundleReference();
        abRef._assetbundle = ab;
        abRef._path = abPath;
        _loadedAssetBundles.Add(abPath, abRef);
        if (isDep)
        {
            abRef.Depend();
        }
    }

    private IEnumerator _LoadAssetBundleAsync(string abPath, Action<AssetBundle> callback)
    {
        //尝试加载，不会产出依赖计数
        yield return _LoadAssetBundleInternalAsync(abPath,false);

        //尝试加载依赖，所有的加载会产生依赖计数
        string[] deps = _manifest.GetAllDependencies(abPath);
        foreach (string dep in deps)
        {
            //一个加载完后，再加载下一个
            yield return _LoadAssetBundleInternalAsync(dep, true);
        }

        if (callback != null)
        {
            AssetBundleReference abRef;
            if (!_loadedAssetBundles.TryGetValue(abPath, out abRef))
            {
                callback(null);
                yield break;
            }
            callback(abRef._assetbundle);
        }
    }

    public AssetBundle LoadAssetBundle(string abPath)
    {
        //尝试加载，不会产出依赖计数
        _LoadAssetBundleInternal(abPath,false);

        string[] deps = _manifest.GetAllDependencies(abPath);
        foreach (string dep in deps)
        {
            _LoadAssetBundleInternal(dep, true);
        }

        AssetBundleReference abRef;
        if (!_loadedAssetBundles.TryGetValue(abPath, out abRef))
        {
            return null;
        }
        return abRef._assetbundle;
    }

    private bool _UnloadAssetBundle(string abPath,bool isDep)
    {
        if (_loadingAssetBundles.Contains(abPath))
        {
            //还在加载，删除，并返回
            _loadingAssetBundles.Remove(abPath);
            return true;
        }

        AssetBundleReference abRef;
        if (!_loadedAssetBundles.TryGetValue(abPath, out abRef))
        {
            return false;
        }

        if (isDep)
        {
            abRef.UnDepend(); //释放依赖
        }

        if (abRef.HasDepend()) //被依赖
        {
            return false;
        }

        if (abRef.HasReference()) //被引用
        {
            return false;
        }

        abRef._assetbundle.Unload(true);
        abRef._assetbundle = null;
        _loadedAssetBundles.Remove(abPath);
        return true;
    }

    public string GetFullPath(string abPath)
    {
        return Path.Combine(Application.streamingAssetsPath, abPath);
    }

}

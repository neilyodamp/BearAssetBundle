using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;
using System.IO;

public class AssetBundleLoader : MonoBehaviour {

    private sealed class ABRef
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

    private AssetBundleManifest _mainfest;
    private Dictionary<string, ABRef> _loadedAB;
    private HashSet<string> _loadingABs;
    private HashSet<string> _loadingAssets;

    public static AssetBundleLoader Instance;

    public static void Create()
    {
        GameObject go = new GameObject("AssetBundleLoader");
        Instance = go.AddComponent<AssetBundleLoader>();
        DontDestroyOnLoad(go);
    }

    public Coroutine StartInit()
    {
        return StartCoroutine((Init()));
    }

    public void Awake()
    {
        _loadedAB = new Dictionary<string, ABRef>();
        _loadingABs = new HashSet<string>();
        _loadingAssets = new HashSet<string>();
    }
    public void OnDestroy()
    {
        Instance = null;
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
            _mainfest = asset as AssetBundleManifest;
        });
    }

    public Coroutine StartLoadAB(string abPath,Action<AssetBundle> callback = null)
    {
        return StartCoroutine(_LoadABAsync(abPath, callback));
    }

    public IEnumerator LoadABAsync(string abPath,Action<AssetBundle> callback =null)
    {
        return _LoadABAsync(abPath,callback);
    }

    public void UnloadAB(string abPath,bool unloadDependenices = true)
    {
        bool isUnload = _UnloadAB(abPath, false);

        if (!isUnload)
            return;

        if(unloadDependenices)
        {
            string[] deps = _mainfest.GetAllDependencies(abPath);
            foreach(string dep in deps)
            {
                _UnloadAB(dep, true);
            }
        }
    }

    public bool HasRef(string abPath)
    {
        ABRef abRef;
        if (_loadedAB.TryGetValue(abPath, out abRef))
            return false;

        return abRef.HasRef();
    }

    public bool IsLoading(string abPath)
    {
        return _loadingABs.Contains(abPath);
    }

    public void RefAB(string abPath)
    {
        if (String.IsNullOrEmpty(abPath))
            return;
        ABRef abRef;
        if (_loadedAB.TryGetValue(abPath, out abRef))
            return;

        abRef.Ref();
    }

    public void UnRefAB(string abPath)
    {
        if(String.IsNullOrEmpty(abPath))
        {
            return;
        }
        ABRef abRef;
        if(!_loadedAB.TryGetValue(abPath,out abRef))
        {
            return;
        }
        abRef.UnRef();
    }

    public Coroutine StartLoadFromAB(string abPath,string assetName,Action<UnityEngine.Object> callback = null)
    {
        return StartCoroutine(_LoadFromABAsync(abPath, assetName, callback));
    }

    public IEnumerator LoadFromABAsync(string abPath,string assetName,Action<UnityEngine.Object> callback = null)
    {
        return _LoadFromABAsync(abPath, assetName, callback);
    }

    private IEnumerator _LoadFromABAsync(string abPath,string assetName,Action<UnityEngine.Object> callback)
    {
        ABRef abRef;
        if(_loadedAB.TryGetValue(abPath,out abRef))
        {
            yield return _LoadABAsync(abPath, null);
            if(_loadedAB.TryGetValue(abPath,out abRef))
            {
                if (callback != null)
                    callback(null);

                yield break;
            }
        }

        string key = string.Format("{0}@{1}", abPath, assetName);
        if(_loadingAssets.Contains(key))
        {
            while (_loadingAssets.Contains(key))
            {
                yield return null;
            }

            UnityEngine.Object asset = abRef.ab.LoadAsset(key);

            if(callback != null)
            {
                callback(asset);
            }
            yield break;
        }

        _loadingAssets.Add(key);
        AssetBundleRequest abRequest = abRef.ab.LoadAssetAsync(assetName);
        yield return abRequest;
        _loadingAssets.Remove(key);
        if(abRequest.asset == null)
        {
            Debug.LogWarningFormat("Not found {0} in {1}", assetName, abPath);
            yield break;
        }
        if(callback != null)
        {
            callback(abRequest.asset);
        }
    }

    public UnityEngine.Object LoadFromAB(string abPath,string assetName)
    {
        ABRef abRef;
        AssetBundle ab;

        if(!_loadedAB.TryGetValue(abPath,out abRef))
        {
            ab = LoadAB(abPath);

            if(ab == null)
            {
                return null;
            }
        }
        else
        {
            ab = abRef.ab;
        }

        UnityEngine.Object asset = ab.LoadAsset(assetName);
        if(asset == null)
        {
            Debug.LogWarningFormat("No found {0} in {1}", assetName, abPath);
            return null;
        }
        return asset;
    }

    private IEnumerator _LoadABInternalAsync(string abPath,bool isDep)
    {
        ABRef abRef;
        if(_loadedAB.TryGetValue(abPath,out abRef))
        {
            if (isDep)
            {
                abRef.RefDep();
            }
            yield break;
        }

        if(_loadingABs.Contains(abPath))
        {
            while (_loadingABs.Contains(abPath))
                yield return null;

            if(isDep)
            {
                if(_loadedAB.TryGetValue(abPath,out abRef))
                {
                    abRef.RefDep();
                }
            }
            yield break;
        }

        _loadingABs.Add(abPath);

        AssetBundleCreateRequest request = AssetBundle.LoadFromFileAsync(GetFullPath(abPath));
        yield return request;

        if(request.assetBundle == null)
        {
            if (_loadingABs.Contains(abPath))
                _loadingABs.Remove(abPath);
            yield break;
        }

        abRef = new ABRef();
        abRef.ab = request.assetBundle;
        abRef.path = abPath;
        _loadedAB.Add(abPath, abRef);

        if(_loadingABs.Contains(abPath))
        {
            if(isDep)
            {
                abRef.RefDep();
            }
            _loadingABs.Remove(abPath);
        }
    }

    private void _LoadABInternal(string abPath, bool isDep)
    {
        ABRef abRef;
        if(_loadedAB.TryGetValue(abPath,out abRef))
        {
            if(isDep)
            {
                abRef.RefDep();
            }
            return;
        }

        AssetBundle ab = AssetBundle.LoadFromFile(GetFullPath(abPath));
        if (ab == null)
            return;

        abRef = new ABRef();
        abRef.ab = ab;
        abRef.path = abPath;
        _loadedAB.Add(abPath, abRef);
        if(isDep)
        {
            abRef.RefDep();
        }
    }

    private IEnumerator _LoadABAsync(string abPath,Action<AssetBundle> callback)
    {
        yield return _LoadABInternalAsync(abPath, false);

        string[] deps = _mainfest.GetAllDependencies(abPath);
        foreach(string dep in deps)
        {
            yield return _LoadABInternalAsync(dep, true);
        }
        if(callback != null)
        {
            ABRef abRef;
            if(!_loadedAB.TryGetValue(abPath,out abRef))
            {
                callback(null);
                yield break;
            }
            callback(abRef.ab);
        }
    }
    
    public AssetBundle LoadAB(string abPath)
    {
        _LoadABInternal(abPath, false);

        string[] deps = _mainfest.GetAllDependencies(abPath);
        foreach(string dep in deps)
        {
            _LoadABInternal(dep, true);
        }

        ABRef abRef;
        if(!_loadedAB.TryGetValue(abPath,out abRef))
        {
            return null;
        }
        return abRef.ab;
    }

    private bool _UnloadAB(string abPath,bool isDep)
    {
        if(_loadingABs.Contains(abPath))
        {
            _loadingABs.Remove(abPath);
            return true;
        }

        ABRef abRef;
        if(!_loadedAB.TryGetValue(abPath,out abRef))
        {
            return false;
        }

        if(!isDep)
        {
            abRef.UnRefDep();
        }

        if(abRef.HasDepRef())
        {
            return false;
        }

        if(abRef.HasRef())
        {
            return false;
        }

        abRef.ab.Unload(true);
        abRef.ab = null;
        _loadedAB.Remove(abPath);
        return true;
    }
    
    public string GetFullPath(string abPath)
    {
        return Path.Combine(Application.streamingAssetsPath, abPath);
    }

}

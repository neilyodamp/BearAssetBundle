using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

public class ResourcesLoader : MonoBehaviour
{
    private HashSet<string> _loadingAssets;

    public static ResourcesLoader Instance;

    public static void Create()
    {
        GameObject go = new GameObject("ResLoader");
        go.AddComponent<ResourcesLoader>();
        DontDestroyOnLoad(go);
    }

    void Awake()
    {
        _loadingAssets = new HashSet<string>();
        Instance = this;
    }

    void OnDestroy()
    {
        Instance = null;
    }

    public Object Load(string path)
    {
        return Resources.Load(path);
    }

    public Coroutine StartLoadAsync(string path, Action<Object> callback = null)
    {
        return StartCoroutine(LoadAsync(path, callback));
    }

    public IEnumerator LoadAsync(string path, Action<Object> callback = null)
    {
        if (_loadingAssets.Contains(path))
        {
            while (_loadingAssets.Contains(path))
            {
                yield return null;
            }
            //如果是加载完成后的资源 可以同步的读出
            Object asset = Resources.Load(path);
            if (callback != null)
            {
                callback(asset);
            }
            yield break;
        }

        _loadingAssets.Add(path);
        var req = Resources.LoadAsync(path);
        yield return req;
        _loadingAssets.Remove(path);
        if (callback != null)
        {
            callback(req.asset);
        }
    }
}

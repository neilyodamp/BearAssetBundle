using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

public class ResourcesLoader : MonoBehaviour
{
    private HashSet<string> mLoadingAssets;

    public static ResourcesLoader Ins;

    public static void Create()
    {
        GameObject go = new GameObject("ResLoader");
        go.AddComponent<ResourcesLoader>();
        DontDestroyOnLoad(go);
    }

    void Awake()
    {
        mLoadingAssets = new HashSet<string>();
        Ins = this;
    }

    void OnDestroy()
    {
        Ins = null;
    }

    public Object Load(string path)
    {
        return Resources.Load(path);
    }

    public Coroutine StartLoadAsync(string path, Action<Object> cb = null)
    {
        return StartCoroutine(LoadAsync(path, cb));
    }

    public IEnumerator LoadAsync(string path, Action<Object> cb = null)
    {
        if (mLoadingAssets.Contains(path))
        {
            while (mLoadingAssets.Contains(path))
            {
                yield return null;
            }
            Object asset = Resources.Load(path);
            if (cb != null)
            {
                cb(asset);
            }
            yield break;
        }

        mLoadingAssets.Add(path);
        var req = Resources.LoadAsync(path);
        yield return req;
        mLoadingAssets.Remove(path);
        if (cb != null)
        {
            cb(req.asset);
        }
    }
}

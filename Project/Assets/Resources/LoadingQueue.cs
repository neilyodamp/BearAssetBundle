using System;
using System.Collections.Generic;
using UnityEngine;
using System.Collections;

public interface ILoadingQueue
{
    void AddLoadRequest(LoadRequest req);
    IEnumerator Load();
    void SetTaskLoader(Action<LoadTask> loader);
    void SetTaskAsyncLoader(Action<LoadTask> loader);
    void SetRequestLoaded(Action<LoadRequest> loaded);
    void LoadTask(LoadTask task);
    void LoadTaskAsync(LoadTask task);
    void OnRequestLoaded(LoadRequest req);
}

public abstract class AbsLoadingQueue : ILoadingQueue
{
    protected Queue<LoadRequest> _loadingQueue;

    protected AbsLoadingQueue()
    {
        _loadingQueue = new Queue<LoadRequest>();
    }

    public Queue<LoadRequest> GetQueue()
    {
        return _loadingQueue;
    }

    public void AddLoadRequest(LoadRequest req)
    {
        _loadingQueue.Enqueue(req);
    }
    public abstract IEnumerator Load();

    private Action<LoadTask> _loader;
    private Action<LoadTask> _asyncLoader;
    private Action<LoadRequest> _onLoaded; 

    public void SetTaskLoader(Action<LoadTask> loader)
    {
        this._loader = loader;
    }

    public void SetTaskAsyncLoader(Action<LoadTask> loader)
    {
        this._asyncLoader = loader;
    }

    public void SetRequestLoaded(Action<LoadRequest> onLoaded)
    {
        this._onLoaded = onLoaded;
    }

    public void LoadTask(LoadTask task)
    {
        this._loader(task);
    }

    public void LoadTaskAsync(LoadTask task)
    {
        this._asyncLoader(task);
    }

    public void OnRequestLoaded(LoadRequest req)
    {
        this._onLoaded(req);
    }
}

//同步加载队列，每个任务都是同步加载
public class LoadingQueue : AbsLoadingQueue
{
    public override IEnumerator Load()
    {
        while (_loadingQueue.Count > 0)
        {
            LoadRequest req = _loadingQueue.Dequeue();
            if (req.IsDone || req.IsCancel)
            {
                continue;
            }
            //全部提交
            foreach (LoadTask task in req)
            {
                LoadTask(task);
            }
            OnRequestLoaded(req);
            yield return null;
        }
    }
}

//异步加载,整个请求的所有任务一次性全提交，全部任务加载完毕，加载下一个请求
public class AsyncLoadingQueue : AbsLoadingQueue
{
    public override IEnumerator Load()
    {
        while (_loadingQueue.Count > 0)
        {
            LoadRequest req = _loadingQueue.Dequeue();
            if (req.IsDone || req.IsCancel)
            {
                continue;
            }
            //全部提交
            foreach (LoadTask task in req)
            {
                LoadTaskAsync(task);
            }
            //等待整个请求结束
            yield return new LoadReqAsync(req);
            OnRequestLoaded(req);
        }
    }
}

//异步加载，整个请求的任务，完成一个再提交下一个
public class AsyncLoadingQueueSeq : AbsLoadingQueue
{
    public override IEnumerator Load()
    {
        while (_loadingQueue.Count > 0)
        {
            LoadRequest req = _loadingQueue.Dequeue();
            if (req.IsDone || req.IsCancel)
            {
                continue;
            }
            //一次提交一个，加载完再次提交
            foreach (LoadTask task in req)
            {
                LoadTaskAsync(task);
                yield return new LoadTaskAsync(task);
                //加载完一个任务后发现整个请求取消了
                if (req.IsCancel)
                    break;
            }
            OnRequestLoaded(req);
        }
    }
}
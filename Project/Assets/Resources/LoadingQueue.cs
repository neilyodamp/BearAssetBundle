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
    protected Queue<LoadRequest> mLoadingQueue;

    protected AbsLoadingQueue()
    {
        mLoadingQueue = new Queue<LoadRequest>();
    }

    public Queue<LoadRequest> GetQueue()
    {
        return mLoadingQueue;
    }

    public void AddLoadRequest(LoadRequest req)
    {
        mLoadingQueue.Enqueue(req);
    }
    public abstract IEnumerator Load();

    private Action<LoadTask> mLoader;
    private Action<LoadTask> mAsyncLoader;
    private Action<LoadRequest> mOnLoaded; 

    public void SetTaskLoader(Action<LoadTask> loader)
    {
        this.mLoader = loader;
    }

    public void SetTaskAsyncLoader(Action<LoadTask> loader)
    {
        this.mAsyncLoader = loader;
    }

    public void SetRequestLoaded(Action<LoadRequest> onLoaded)
    {
        this.mOnLoaded = onLoaded;
    }

    public void LoadTask(LoadTask task)
    {
        this.mLoader(task);
    }

    public void LoadTaskAsync(LoadTask task)
    {
        this.mAsyncLoader(task);
    }

    public void OnRequestLoaded(LoadRequest req)
    {
        this.mOnLoaded(req);
    }
}

//同步加载队列，每个任务都是同步加载
public class LoadingQueue : AbsLoadingQueue
{
    public override IEnumerator Load()
    {
        while (mLoadingQueue.Count > 0)
        {
            LoadRequest req = mLoadingQueue.Dequeue();
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
        while (mLoadingQueue.Count > 0)
        {
            LoadRequest req = mLoadingQueue.Dequeue();
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
        while (mLoadingQueue.Count > 0)
        {
            LoadRequest req = mLoadingQueue.Dequeue();
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
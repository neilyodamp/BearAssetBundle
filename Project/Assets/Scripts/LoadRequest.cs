using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zeus
{
    //加载任务
    public class LoadTask
    {
        public string Tag { get; set; }
        public string Path { get; set; }
        public Object LoadedAsset { get; set; } //加载到的资源
        public bool IsDone { get; set; } //是否加载完毕，可能IsDone == true,但是LoadedAsset == null,说明加载结束，但是加载失败了
                                         //包含该Task的Request
        public LoadRequest Request { get; set; }

        public LoadTask()
        {
            IsDone = false;
        }

        public LoadTask(string tag, string path)
        {
            this.Tag = tag;
            this.Path = path;
        }
    }

    //异步辅助
    public class LoadTaskAsync : IEnumerator
    {
        public LoadTask Task { get; set; }

        public LoadTaskAsync(LoadTask task)
        {
            Task = task;
        }

        public bool MoveNext()
        {
            if (Task == null)
            {
                return true;
            }

            return !Task.IsDone;
        }

        public void Reset()
        {
        }

        public object Current
        {
            get { return null; }
        }
    }

    //异步辅助类
    public class LoadReqAsync : IEnumerator
    {
        public LoadRequest Req { get; set; }

        public LoadReqAsync(LoadRequest req)
        {
            Req = req;
        }

        public bool MoveNext()
        {
            if (Req == null)
                return true;

            return !Req.IsDone && !Req.IsCancel;
        }

        public void Reset()
        {
        }

        public object Current
        {
            get { return null; }
        }
    }

    //加载请求，加载任务的容器
    public class LoadRequest : IEnumerable<LoadTask>
    {
        //加载列表，按照添加任务的顺序执行加载
        private List<LoadTask> _loadTasks;
        //Tag映射，加速查找
        private Dictionary<string, int> _tagIndexTable;

        public bool IsDone
        {
            get { return LoadedCount() >= TaskCount(); }
        }

        public bool IsCancel { get; set; }
        public delegate void OnAllLoaded(LoadRequest req);
        public OnAllLoaded onAllLoaded { get; set; }

        public LoadRequest()
        {
            _loadTasks = new List<LoadTask>();
            _tagIndexTable = new Dictionary<string, int>();
        }

        public void AddTask(string tag, string path)
        {
            LoadTask task = new LoadTask(tag, path);
            AddTask(task);
        }

        public void AddTask(LoadTask task)
        {
            if (_tagIndexTable.ContainsKey(task.Tag))
            {
                Debug.LogError("Same Tag already exist:" + task.Tag);
                return;
            }

            _loadTasks.Add(task);
            task.Request = this;
            _tagIndexTable[task.Tag] = _loadTasks.Count - 1;
        }

        public LoadTask GetTaskByTag(string tag)
        {
            int index;
            if (!_tagIndexTable.TryGetValue(tag, out index))
            {
                return null;
            }
            return _loadTasks[index];
        }

        public Object GetLoadedResByTag(string tag)
        {
            LoadTask task = GetTaskByTag(tag);
            if (task == null)
            {
                return null;
            }
            return task.LoadedAsset;
        }

        public void Cancel()
        {
            IsCancel = true;
        }

        public int TaskCount()
        {
            return _loadTasks.Count;
        }

        //已经加载的数量
        public int LoadedCount()
        {
            int c = 0;
            foreach (var task in _loadTasks)
            {
                c += task.IsDone ? 1 : 0;
            }
            //mLoadTasks.ForEach(task => c += task.IsDone ? 1 : 0);
            return c;
        }

        public IEnumerator<LoadTask> GetEnumerator()
        {
            return _loadTasks.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public void CallAllLoaded()
        {
            if (onAllLoaded != null)
                onAllLoaded(this);
        }

        public void Clear()
        {
            _loadTasks.Clear();
        }
    }
}
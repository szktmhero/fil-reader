using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using FileReader.Services;
using FileReader.Models;

namespace FileReader.Collections
{
    public class VirtualizingList : IList, INotifyCollectionChanged, INotifyPropertyChanged
    {
        private readonly LogDatabase _database;
        private readonly List<SearchCondition> _conditions;
        private readonly int _pageSize;
        private int _count = -1;

        // 簡易的なページキャッシュ (PageNumber -> PageData)
        // メモリリーク防止のため、最大キャッシュ保持数を制限 (例: 5ページ分)
        private readonly Dictionary<int, List<Dictionary<string, string>>> _pageCache = new();
        private readonly Queue<int> _cacheOrder = new();
        private const int MaxCachePages = 50;

        public event NotifyCollectionChangedEventHandler? CollectionChanged;
        public event PropertyChangedEventHandler? PropertyChanged;

        public VirtualizingList(LogDatabase database, List<SearchCondition> conditions, int pageSize = 100)
        {
            _database = database;
            _conditions = conditions ?? new List<SearchCondition>();
            _pageSize = pageSize;
        }

        public object? this[int index]
        {
            get
            {
                if (index < 0 || index >= Count)
                    throw new ArgumentOutOfRangeException(nameof(index));

                int pageNumber = index / _pageSize;
                int pageOffset = index % _pageSize;

                if (!_pageCache.TryGetValue(pageNumber, out var page))
                {
                    // データベースからページデータをロード
                    int dbOffset = pageNumber * _pageSize;
                    page = _database.GetPage(dbOffset, _pageSize, _conditions);
                    
                    // キャッシュに格納
                    _pageCache[pageNumber] = page;
                    _cacheOrder.Enqueue(pageNumber);

                    // キャッシュ上限を超えたら古いものを削除
                    if (_pageCache.Count > MaxCachePages)
                    {
                        int oldestPage = _cacheOrder.Dequeue();
                        _pageCache.Remove(oldestPage);
                    }
                }

                if (pageOffset < page.Count)
                {
                    return page[pageOffset];
                }

                // 破損行などで行が足りない場合は空行を返す
                return new Dictionary<string, string>();
            }
            set => throw new NotSupportedException();
        }

        public int Count
        {
            get
            {
                if (_count == -1)
                {
                    _count = _database.GetTotalCount(_conditions);
                }
                return _count;
            }
        }

        public bool IsReadOnly => true;
        public bool IsFixedSize => true;
        public bool IsSynchronized => false;
        public object SyncRoot => this;

        public int Add(object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(object? value) => false;
        public int IndexOf(object? value) => -1;
        public void Insert(int index, object? value) => throw new NotSupportedException();
        public void Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();

        public void CopyTo(Array array, int index)
        {
            for (int i = 0; i < Count; i++)
            {
                array.SetValue(this[i], index + i);
            }
        }

        public IEnumerator GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        public void Refresh()
        {
            _count = -1;
            _pageCache.Clear();
            _cacheOrder.Clear();
            
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        }
    }
}

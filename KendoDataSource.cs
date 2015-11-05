using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Flurl;
using Flurl.Http;
using Flurl.Http.Configuration;
using Kendo.Mvc.Extensions;
using Newtonsoft.Json;
using Telerik.Windows.Controls;
using Telerik.Windows.Controls.GridView;
using Telerik.Windows.Data;
using System.Collections;
using System.Collections.ObjectModel;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace Teamnet.Wpf.UI
{
    using MvcFilterOperator = Kendo.Mvc.FilterOperator;
    using ObjectEnumerable = IEnumerable<object>;

    public class KendoDataSource<TEntity> : QueryableCollectionView where TEntity : class
    {        
        private readonly string uri;
        private int itemCount;
        private bool suspendNotifications;

        public KendoDataSource(Uri uri, int pageSize = 10) : base(new RadObservableCollection<object>(new object[pageSize]))
        {
            this.uri = uri.ToString();
            PageSize = pageSize;
        }

        private RadObservableCollection<object> Source => (RadObservableCollection<object>) SourceCollection;

        public override int ItemCount => itemCount;

        protected override int GetPagingDeterminativeItemCount() => itemCount;

        protected override IQueryable CreateView() => QueryableSourceCollection;

        public void SuspendNotifications()
        {
            suspendNotifications = true;
        }

        public void ResumeNotifications()
        {
            suspendNotifications = false;
        }

        protected async override void OnCollectionChanged(NotifyCollectionChangedEventArgs args)
        {
            if(suspendNotifications)
            {
                return;
            }
            if(args != null && args.Action == NotifyCollectionChangedAction.Reset)
            {
                SuspendNotifications();
                Source.SuspendNotifications();
                for(int index = 0; index < PageSize; index++)
                {
                    Source[index] = null;
                }
                await LoadData();
                Source.ResumeNotifications();
                ResumeNotifications();
            }
            base.OnCollectionChanged(args);
            var list = InternalList;
        }

        private async Task LoadData()
        {
            var result = await this.GetData<TEntity>(uri);
            TotalItemCount = itemCount = result.Total;
            for(int index = 0; index < result.Items.Length; index++)
            {
                if(IsGrouped)
                {
                    InternalList.Add(result.Items[index]);
                }
                else
                {
                    Source[index] = result.Items[index];
                }
            }
            //for(int index = result.Items.Length; index < PageSize; index++)
            //{
            //    Source[index] = null;
            //}
        }

        protected override void OnFilterDescriptorsChanged()
        {
            PageIndex = 0;
            base.OnFilterDescriptorsChanged();
        }

        public void HandleDistinctValues(GridViewDistinctValuesLoadingEventArgs e) => this.HandleDistinctValues(e, uri);
    }

    public static class Helper
    {
        public class CustomHttpClientFactory : DefaultHttpClientFactory
        {
            public override HttpClient CreateClient(Url url, HttpMessageHandler handler)
            {
                ((HttpClientHandler)handler).UseDefaultCredentials = true;
                return base.CreateClient(url, handler);
            }
        }

        static Helper()
        {
            FlurlHttp.Configuration.HttpClientFactory = new CustomHttpClientFactory();
        }

        public static async void HandleDistinctValues(this QueryableCollectionView view, GridViewDistinctValuesLoadingEventArgs args, string uri)
        {
            if(SetStaticValues(args))
            {
                return;
            }
            var collection = new RadObservableCollection<object>();
            args.ItemsSource = collection;
            var result = await view.GetDistinctValues(args, uri);
            collection.AddRange(result);
        }

        private static bool SetStaticValues(GridViewDistinctValuesLoadingEventArgs args)
        {
            bool nullable;
            var columnType = GetColumnType(args, out nullable);
            Array values = null;
            if(columnType.IsEnum)
            {
                values = Enum.GetValues(columnType);
            }
            else if(columnType == typeof(bool))
            {
                values = new object[] { true, false };
            }
            if(values == null)
            {
                return false;
            }
            if(nullable)
            {
                values = new object[] { null }.Concat(values.Cast<object>()).ToArray();
            }
            args.ItemsSource = values;
            return true;
        }

        private static Task<ObjectEnumerable> GetDistinctValues(this QueryableCollectionView view, GridViewDistinctValuesLoadingEventArgs args, string uri)
        {
            return uri
                        .AppendPathSegment("GetDistinctValues")
                        .SetQueryParam("columnName", args.Column.UniqueName)
                        .SetQueryParam("filter", view.GetFilter())
                        .GetJsonAsync<ObjectEnumerable>();
        }

        private static Type GetColumnType(GridViewDistinctValuesLoadingEventArgs args, out bool nullable)
        {
            var column = (GridViewBoundColumnBase)args.Column;
            var originalType = column.DataType;
            var underlyingType = Nullable.GetUnderlyingType(column.DataType);
            if(underlyingType == null)
            {
                nullable = false;
                return originalType;
            }
            nullable = true;
            return underlyingType;
        }

        public static async Task<IDataSourceResult> GetData<TEntity>(this QueryableCollectionView view, string uri) where TEntity : class
        {
            var url = uri
                        .SetQueryParam("sort", view.GetSort())
                        .SetQueryParam("page", view.PageIndex + 1)
                        .SetQueryParam("pageSize", view.PageSize)
                        .SetQueryParam("filter", view.GetFilter())
                        .SetQueryParam("group", view.GetGroup());
            return view.IsGrouped ? (IDataSourceResult) await url.GetJsonAsync<DataSourceResult<GroupData<TEntity>>>() : await url.GetJsonAsync<DataSourceResult<TEntity>>();
        }

        private static string GetSort(this QueryableCollectionView view)
        {
            if(view.SortDescriptors.Count == 0)
            {
                return string.Empty;
            }
            var sortDescriptor = (ColumnSortDescriptor)view.SortDescriptors[0];
            return sortDescriptor.Column.UniqueName + "-" + ToString(sortDescriptor.SortDirection);
        }

        private static string ToString(ListSortDirection? sortDirection)
        {
            if(sortDirection == null)
            {
                return string.Empty;
            }
            return sortDirection == ListSortDirection.Ascending ? "asc" : "desc";
        }

        private static string GetGroup(this QueryableCollectionView view)
        {
            return string.Join("~", view.GroupDescriptors.OfType<ColumnGroupDescriptor>().Select(g => g.Column.UniqueName+ "-" + ToString(g.SortDirection)));
        }

        private static string GetFilter(this QueryableCollectionView view)
        {
            var filterDescriptors = view.FilterDescriptors.OfType<IColumnFilterDescriptor>().SelectMany(GetFilter);
            return string.Join("~and~", filterDescriptors);
        }

        private static IEnumerable<string> GetFilter(IColumnFilterDescriptor columnFilter)
        {
            var columnName = columnFilter.Column.UniqueName;
            var fieldFilter = columnFilter.FieldFilter;
            if(fieldFilter.Filter1.IsActive)
            {
                yield return fieldFilter.Filter2.IsActive ? GetCompositeFilter(columnName, fieldFilter) : GetSimpleFilter(columnName, fieldFilter.Filter1);
            }
            else if(fieldFilter.Filter2.IsActive)
            {
                yield return GetSimpleFilter(columnName, fieldFilter.Filter2);
            }
            if(columnFilter.DistinctFilter.IsActive)
            {
                var distinctFilters = columnFilter.DistinctFilter.FilterDescriptors.Select(distinctFilter => GetSimpleFilter(columnName, distinctFilter));
                yield return "("+string.Join("~or~", distinctFilters) +")";
            }
        }

        private static string GetCompositeFilter(string member, IFieldFilterDescriptor fieldFilter)
        {
            var op = fieldFilter.LogicalOperator.ToString().ToLower();
            return $"({GetSimpleFilter(member, fieldFilter.Filter1)}~{op}~{GetSimpleFilter(member, fieldFilter.Filter2)})";
        }

        private static string GetSimpleFilter(string member, OperatorValueFilterDescriptorBase fieldFilter)
        {
            var filterOperator = ToMvcFilterOperator(fieldFilter.Operator).ToToken();
            var value = JsonConvert.ToString(fieldFilter.Value).Trim('"');
            return $"{member}~{filterOperator}~'{value}'";
        }

        private static MvcFilterOperator ToMvcFilterOperator(FilterOperator filterOperator)
        {
            if(filterOperator < FilterOperator.DoesNotContain)
            {
                return (MvcFilterOperator)filterOperator;
            }
            if(filterOperator == FilterOperator.DoesNotContain)
            {
                return MvcFilterOperator.DoesNotContain;
            }
            throw new NotImplementedException();
        }
    }

    public interface IDataSourceResult
    {
        IEnumerable<AggregateResult> AggregateResults { get; set; }
        object Errors { get; set; }
        int Total { get; set; }

        object[] Items { get; }
    }

    public class GroupData<TEntity> : Group
    {
        public new TEntity[] Items { get; set; }
    }

    //public class GroupData<TEntity> : IGroup
    //{
    //    public bool HasSubgroups { get; set; }

    //    public int ItemCount { get; set; }

    //    public TEntity[] Items { get; set; }

    //    public object Key { get; set; }

    //    public IGroup ParentGroup { get; set; }

    //    public ReadOnlyCollection<IGroup> Subgroups { get; set; }

    //    IEnumerable IGroup.Items
    //    {
    //        get
    //        {
    //            return Items;
    //        }
    //    }
    //}

    public class DataSourceResult<TItem> : IDataSourceResult where TItem : class
    {
        public IEnumerable<AggregateResult> AggregateResults { get; set; }
        public TItem[] Data { get; set; }
        public object Errors { get; set; }
        public int Total { get; set; }

        public object[] Items
        {
            get
            {
                return Data;
            }
        }
    }
}
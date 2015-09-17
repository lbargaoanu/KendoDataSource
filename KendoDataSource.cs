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

namespace Teamnet.Wpf.UI
{
    using MvcFilterOperator = Kendo.Mvc.FilterOperator;
    using ObjectEnumerable = IEnumerable<object>;

    public class KendoDataSource<TEntity> : QueryableCollectionView
    {
        private readonly string uri;
        private int itemCount;

        public KendoDataSource(Uri uri, int pageSize = 10) : base(new RadObservableCollection<TEntity>())
        {
            this.uri = uri.ToString();
            PageSize = itemCount = pageSize;
        }

        private RadObservableCollection<TEntity> Source => (RadObservableCollection<TEntity>) SourceCollection;

        public override int ItemCount => itemCount;

        public override int TotalItemCount => itemCount;

        protected override int GetPagingDeterminativeItemCount() => itemCount;

        protected async override void OnCollectionChanged(NotifyCollectionChangedEventArgs args)
        {
            if(args != null && args.Action == NotifyCollectionChangedAction.Reset)
            {
                await LoadData();
            }
            base.OnCollectionChanged(args);
        }

        private async Task LoadData()
        {
            var result = await this.GetData<TEntity>(uri);
            var offset = PageIndex * PageSize;
            var missingCount = offset + result.Data.Length - Source.Count;
            if(missingCount > 0)
            {
                for(int index = 0; index < missingCount; index++)
                {
                    Source.Add(default(TEntity));
                }
            }
            for(int index = 0; index < result.Data.Length; index++)
            {
                Source.Insert(offset + index, result.Data[index]);
            }
            itemCount = result.Total;
        }

        protected override void OnFilterDescriptorsChanged()
        {
            base.OnFilterDescriptorsChanged();
            Refresh();
        }

        public void HandleDistinctValues(GridViewDistinctValuesLoadingEventArgs e) => this.HandleDistinctValues(e, uri);
    }

    public class KendoVirtualDataSource<TEntity> : VirtualQueryableCollectionView
    {
        private readonly string uri;

        public KendoVirtualDataSource(Uri uri, int pageSize = 10)
        {
            this.uri = uri.ToString();
            VirtualItemCount = 100;
            LoadSize = pageSize;
            ItemsLoading += OnItemsLoading;
        }

        private async void OnItemsLoading(object sender, VirtualQueryableCollectionViewItemsLoadingEventArgs e)
        {
            var result = await this.GetData<TEntity>(uri);
            VirtualItemCount = result.Total;
            Load(e.StartIndex, result.Data);
        }

        protected override int GetPagingDeterminativeItemCount() => VirtualItemCount;

        protected override void OnFilterDescriptorsChanged()
        {
            base.OnFilterDescriptorsChanged();
            if(VirtualItemCount == 0)
            {
                VirtualItemCount = 100; // refresh
            }
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

        public static Task<EntityDataSourceResult<TEntity>> GetData<TEntity>(this QueryableCollectionView view, string uri)
        {
            return uri
                        .SetQueryParam("sort", view.GetSort())
                        .SetQueryParam("page", view.PageIndex + 1)
                        .SetQueryParam("pageSize", view.PageSize)
                        .SetQueryParam("filter", view.GetFilter())
                        .GetJsonAsync<EntityDataSourceResult<TEntity>>();
        }

        private static string GetFilter(this QueryableCollectionView view)
        {
            var filterDescriptors = view.FilterDescriptors.OfType<IColumnFilterDescriptor>().SelectMany(GetFilter);
            return string.Join("~and~", filterDescriptors);
        }

        private static string GetSort(this QueryableCollectionView view)
        {
            if(view.SortDescriptors.Count == 0)
            {
                return string.Empty;
            }
            var sortDescriptor = (ColumnSortDescriptor)view.SortDescriptors[0];
            var direction = sortDescriptor.SortDirection == ListSortDirection.Ascending ? "asc" : "desc";
            return sortDescriptor.Column.UniqueName + "-" + direction;
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
                foreach(var distinctFilter in columnFilter.DistinctFilter.FilterDescriptors)
                {
                    yield return GetSimpleFilter(columnName, distinctFilter);
                }
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

    public class EntityDataSourceResult<TEntity>
    {
        public IEnumerable<AggregateResult> AggregateResults { get; set; }
        public TEntity[] Data { get; set; }
        public object Errors { get; set; }
        public int Total { get; set; }
    }
}
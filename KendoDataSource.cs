using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Flurl;
using Flurl.Http;
using Kendo.Mvc.Extensions;
using Newtonsoft.Json;
using Telerik.Windows.Controls;
using Telerik.Windows.Controls.GridView;
using Telerik.Windows.Data;

namespace Teamnet.Wpf.UI
{
    using System.Net.Http;
    using Flurl.Http.Configuration;
    using MvcFilterOperator = Kendo.Mvc.FilterOperator;
    using ObjectEnumerable = IEnumerable<object>;

    public class KendoDataSource<TEntity> : VirtualQueryableCollectionView
    {
        private readonly string uri;

        public KendoDataSource(Uri uri, int pageSize = 10)
        {
            this.uri = uri.ToString();
            VirtualItemCount = 100;
            LoadSize = pageSize;
            ItemsLoading += OnItemsLoading;
            CustomHttpClientFactory.EnsureInitialized();
        }

        public async void HandleDistinctValues(GridViewDistinctValuesLoadingEventArgs args)
        {
            if(SetStaticValues(args))
            {
                return;
            }
            var collection = new RadObservableCollection<object>();
            args.ItemsSource = collection;
            var result = await GetDistinctValues(args);
            collection.AddRange(result);
        }

        private bool SetStaticValues(GridViewDistinctValuesLoadingEventArgs args)
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

        private Task<ObjectEnumerable> GetDistinctValues(GridViewDistinctValuesLoadingEventArgs args)
        {
            return uri
                        .AppendPathSegment("GetDistinctValues")
                        .SetQueryParam("columnName", args.Column.UniqueName)
                        .SetQueryParam("filter", GetFilter())
                        .GetJsonAsync<ObjectEnumerable>();
        }

        private Type GetColumnType(GridViewDistinctValuesLoadingEventArgs args, out bool nullable)
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

        private async void OnItemsLoading(object sender, VirtualQueryableCollectionViewItemsLoadingEventArgs e)
        {
            var result = await GetData();
            VirtualItemCount = result.Total;
            Load(e.StartIndex, result.Data);
        }

        private Task<EntityDataSourceResult> GetData()
        {
            return uri
                        .SetQueryParam("sort", GetSort())
                        .SetQueryParam("page", PageIndex + 1)
                        .SetQueryParam("pageSize", PageSize)
                        .SetQueryParam("filter", GetFilter())
                        .GetJsonAsync<EntityDataSourceResult>();
        }

        protected override int GetPagingDeterminativeItemCount()
        {
            // varianta din clasa de baza merge numai cu un IQueryable
            return VirtualItemCount;
        }

        protected override void OnFilterDescriptorsChanged()
        {
            base.OnFilterDescriptorsChanged();
            if(VirtualItemCount == 0)
            {
                VirtualItemCount = 100; // refresh
            }
        }

        private string GetFilter()
        {
            var filterDescriptors = FilterDescriptors.OfType<IColumnFilterDescriptor>().SelectMany(GetFilter);
            return string.Join("~and~", filterDescriptors);
        }

        private string GetSort()
        {
            if(SortDescriptors.Count == 0)
            {
                return string.Empty;
            }
            var sortDescriptor = (ColumnSortDescriptor)SortDescriptors[0];
            var direction = sortDescriptor.SortDirection == ListSortDirection.Ascending ? "asc" : "desc";
            return sortDescriptor.Column.UniqueName + "-" + direction;
        }

        private IEnumerable<string> GetFilter(IColumnFilterDescriptor columnFilter)
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

        class EntityDataSourceResult
        {
            public IEnumerable<AggregateResult> AggregateResults { get; set; }
            public IEnumerable<TEntity> Data { get; set; }
            public object Errors { get; set; }
            public int Total { get; set; }
        }

        public class CustomHttpClientFactory : DefaultHttpClientFactory
        {
            static CustomHttpClientFactory()
            {
                FlurlHttp.Configuration.HttpClientFactory = new CustomHttpClientFactory();
            }

            public override HttpClient CreateClient(Url url, HttpMessageHandler handler)
            {
                ((HttpClientHandler)handler).UseDefaultCredentials = true;
                return base.CreateClient(url, handler);
            }

            public static void EnsureInitialized()
            {
            }
        }
    }
}
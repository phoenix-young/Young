﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Text;
using Young.Data.Attributes;

namespace Young.Data
{
    public delegate void OnSettingPropertyHander(object sender, SetPropertyArgs e);

    public delegate T Convert<T>(DataRow[] rows);

    public class DataDriven
    {
        public event OnSettingPropertyHander OnSettingProperty;

        private static List<string> _privateTables;


        static DataDriven()
        {
            GlobalBindingModeType = new BindingMode();
        }

        public DataDriven()
        {
            me = this.GetType();
        }

        protected Type me;

        private DataBindingAttribute dba;



        public static DataSet Data { get; set; }

        public static int CurrentId { get; set; }

        protected static Dictionary<Type, int> TypeCounts = new Dictionary<Type, int>();

        public static List<string> NonSharedTables
        {

            get { return _privateTables; }
            set
            {
                _privateTables = value;
                addColumnTableMapping();
            }
        }

        public static BindingMode GlobalBindingModeType { get; set; }

        public BindingMode BindingModeType { get; set; }

        private BindingMode _bindingType;

        private static Dictionary<string, string> _tableMapping;

        private static void addColumnTableMapping()
        {
            _tableMapping = new Dictionary<string, string>();
            if (_privateTables == null)
                _privateTables = new List<string>();
            else
                _privateTables.ForEach(s => s = s.ToLower());

            if (Data != null)
            {
                foreach (DataTable table in Data.Tables)
                {
                    if (!_privateTables.Contains(table.TableName.ToLower()))
                    {
                        foreach (DataColumn dc in table.Columns)
                        {
                            if (!_tableMapping.ContainsKey(dc.ColumnName.ToLower()))
                                _tableMapping.Add(dc.ColumnName.ToLower(), table.TableName);
                        }
                    }
                }
            }
        }


        public void ResetIndex()
        {
            if (TypeCounts.ContainsKey(me))
            {
                TypeCounts.Remove(me);
            }
        }

        public void DataBinding()
        {

            if (BindingModeType == null)
            {
                _bindingType = GlobalBindingModeType;
            }
            else
            {
                _bindingType = BindingModeType;
            }

            dba = me.GetCustomAttributes(typeof(DataBindingAttribute), true).FirstOrDefault() as DataBindingAttribute;

            if (dba != null)
            {
                dba.TableName = string.IsNullOrEmpty(dba.TableName) ? me.Name : dba.TableName;

                if (TypeCounts.ContainsKey(me))
                {
                    TypeCounts[me] += 1;
                }
                else
                {
                    TypeCounts.Add(me, 0);
                }

                List<Tuple<MemberInfo, OrderAttribute>> reflectionMembers = null;

                IEnumerable<MemberInfo> members = null;

                switch (_bindingType.SettingMode)
                {
                    case SettingType.PropertyOnly:
                        members = me.GetProperties();
                        break;
                    case SettingType.MethodOnly:
                        members = me.GetMethods();
                        break;
                    default:
                        members = me.GetMembers();
                        break;
                }

                reflectionMembers = members
                    .Where(m => m.GetCustomAttributes(typeof(OrderAttribute), true).FirstOrDefault() != null)
                    .Select(m =>
                    {
                        return new Tuple<MemberInfo, OrderAttribute>(m, m.GetCustomAttributes(typeof(OrderAttribute), true).FirstOrDefault() as OrderAttribute);
                    }).OrderBy(m=>m.Item2.Order).ToList();


                if (_bindingType.IsUsingSampleData)
                {
                    sampleDataBinding(reflectionMembers);
                }
                else
                {
                    tableDataBinding(reflectionMembers);
                }

            }
        }

        private void sampleDataBinding(List<Tuple<MemberInfo, OrderAttribute>> mos)
        {
            foreach (var mo in mos)
            {
                if (mo.Item1 is PropertyInfo)
                {
                    if (_bindingType.LoopMode == LoopType.Loop)
                    {
                        setProperty(mo.Item1 as PropertyInfo, mo.Item2 as ColumnBindingAttribute, TypeCounts[me]);
                    }
                    else
                    {
                        setProperty(mo.Item1 as PropertyInfo, mo.Item2 as ColumnBindingAttribute, 0);
                    }
                }
                else if (mo.Item1 is MethodInfo)
                {
                    invokeMethod(mo.Item1 as MethodInfo);
                }
            }
        }

        private void tableDataBinding(List<Tuple<MemberInfo, OrderAttribute>> mos)
        {
            foreach (var mo in mos)
            {
                if (mo.Item1 is PropertyInfo)
                {
                    PropertyInfo prop = mo.Item1 as PropertyInfo;
                    ColumnBindingAttribute colAttr = mo.Item2 as ColumnBindingAttribute;
                    resetColumnName(mo.Item1, colAttr);
                    bool isSimpleProp = isSimpleField(prop);

                    Func<bool> isSimpleCondition = new Func<bool>(() => isSimpleProp);
                    DataRow[] data = null;
                    if (_bindingType.DataMode == DataType.FromShareTable)
                    {
                        data = getSharedData(colAttr, isSimpleCondition);
                    }
                    else
                    {
                        data = getPrivateData(colAttr, isSimpleCondition);
                    }

                    int index = _bindingType.LoopMode == LoopType.Loop ? TypeCounts[me] : 0;


                    if (isSimpleProp)
                    {
                        if (colAttr.Directory == DataDirectory.Input)
                            setSingleProperty(prop, colAttr, data, index);
                        else
                            getSingleProperty(prop, colAttr, data, index);
                    }
                    else
                    {
                        if (colAttr.Directory == DataDirectory.Input)
                            setComplexProperty(prop, colAttr, data, index);
                    }
                }
                else if (mo.Item1 is MethodInfo)
                {
                    invokeMethod(mo.Item1 as MethodInfo);
                }
            }

        }


        private DataRow[] getSharedData(ColumnBindingAttribute attribute, Func<bool> isSimpleCondition)
        {
            DataTable dt = null;
            string tableName = "";

            string columnName = attribute.ColNames.Last();

            if (_tableMapping.ContainsKey(columnName.ToLower()))
            {
                tableName = _tableMapping[columnName.ToLower()];
            }
            if (tableName != "" && Data.Tables.Contains(tableName))
            {
                dt = Data.Tables[tableName];
            }
            if (dt != null)
            {
                string filter = null;
                filter = getTableFilter(attribute, isSimpleCondition);
                if (filter != null && isColumnsInclude(dt, attribute))
                {
                    return dt.Select(filter);
                }
            }
            return null;
        }

        private DataRow[] getPrivateData(ColumnBindingAttribute attribute, Func<bool> isSimpleCondition)
        {
            DataTable dt = null;
            if (Data.Tables.Contains(dba.TableName))
            {
                dt = Data.Tables[dba.TableName];
            }

            if (dt != null)
            {
                string filter = null;
                filter = getTableFilter(attribute, isSimpleCondition);
                if (filter != null && isColumnsInclude(dt, attribute))
                {
                    return dt.Select(filter);
                }
            }

            return null;
        }

        private bool isSimpleField(PropertyInfo prop)
        {
            if (prop.PropertyType.IsPrimitive || prop.PropertyType == typeof(string))
                return true;
            return false;
        }

        private void resetColumnName(MemberInfo prop, ColumnBindingAttribute attr)
        {
            if (attr.ColNames == null || attr.ColNames.Count() == 0)
            {
                attr.ColNames = new string[] { prop.Name };
            }

        }

        private void getSingleProperty(PropertyInfo prop, ColumnBindingAttribute attribute, DataRow[] rows, int index)
        {
            string colName = attribute.ColNames.First();
            DataRow dr = rows[index];

            dr[colName] = prop.GetValue(this, null);
        }

        private void setSingleProperty(PropertyInfo prop, ColumnBindingAttribute attribute, DataRow[] rows, int index)
        {
            object value = null;
            if (rows != null && rows.Count() > 0)
            {
                string colName = attribute.ColNames.First();

                //May Throw Error
                DataRow dr = rows[index];

                if (dr[colName] != null && dr[colName].ToString() != "")
                {
                    value = getConvertValue(attribute, dr[colName], prop.PropertyType);
                }
            }
            setValue(prop, value, attribute);
        }

        private void setComplexProperty(PropertyInfo prop, ColumnBindingAttribute attribute, DataRow[] rows, int index)
        {
            object value = null;
            value = getConvertValue(attribute, rows, prop.PropertyType);
            setValue(prop, value, attribute);
        }

        private void setValue(PropertyInfo prop, object value, OrderAttribute attribute)
        {
            if (value != null)
            {
                prop.SetValue(this, value, null);
                if (OnSettingProperty != null)
                    OnSettingProperty(this, new SetPropertyArgs(prop, value, attribute));
                runRecusion(value);
            }
        }

        private void setProperty(PropertyInfo prop, ColumnBindingAttribute attribute, int index)
        {
            var propertyType = prop.PropertyType;
            object value = null;

            var singleData = prop.GetCustomAttributes(typeof(SingleSampleDataAttribute), true).Cast<SingleSampleDataAttribute>().Where(d => d.Group == index).FirstOrDefault();
            if (singleData != null)
            {
                value = getConvertValue(attribute, singleData.Value, propertyType);
            }
            else
            {
                var datas = prop.GetCustomAttributes(typeof(ComplexSampleDataAttribute), true).Cast<ComplexSampleDataAttribute>();
                if (datas.Count() > 0)
                {
                    var header = datas.Where(c => c.DataType == SampleDataType.Header && c.Group == index).FirstOrDefault();
                    if (header != null)
                    {
                        DataTable dt = new DataTable();
                        foreach (var s in header.Content)
                        {
                            dt.Columns.Add(new DataColumn(s));
                        }
                        var body = datas.Where(c => c.DataType == SampleDataType.Body && c.Group == index);
                        foreach (var r in body)
                        {
                            var row = dt.NewRow();
                            for (int i = 0; i < r.Content.Count(); i++)
                            {
                                row[i] = r.Content[i];
                            }
                            dt.Rows.Add(row);
                        }
                        var rows = dt.Select();
                        value = getConvertValue(attribute, rows, propertyType);
                    }
                }
            }

            setValue(prop, value, attribute);
        }

        private void invokeMethod(MethodInfo method)
        {
            if (method.GetParameters().Count() == 0)
            {
                dynamic returnObj = method.Invoke(this, null);
                if (method.ReturnType != typeof(void))
                {
                    runRecusion(returnObj);
                }

            }
        }

        private string getTableFilter(ColumnBindingAttribute attribute, Func<bool> isSimplecondition)
        {
            string filter = null;
            if (attribute != null)
            {
                filter = dba.IdColumnName + "=" + CurrentId;
                if (!isSimplecondition())
                {
                    if (_bindingType.LoopMode == LoopType.Loop)
                    {
                        filter += (" and " + attribute.GroupIdColumnName + "=" + TypeCounts[me].ToString());
                    }
                }


            }
            return filter;
        }

        private bool isColumnsInclude(DataTable dt, ColumnBindingAttribute attribute)
        {
            bool isAllContain = true;

            foreach (var s in attribute.ColNames)
            {
                if (dt.Columns.Cast<DataColumn>().Where(c => c.ColumnName.ToLower().Contains(s.ToLower())).FirstOrDefault() == null)
                {
                    isAllContain = false;
                    break;
                }
            }
            return isAllContain;
        }

        private object getConvertValue(ColumnBindingAttribute colAttr, object SourceValue, Type targetType)
        {
            object value = null;
            if (colAttr.MethodName != null && colAttr.Target != null)
            {
                var customDelegate = Delegate.CreateDelegate(typeof(ColumnBindingConvert), colAttr.Target, colAttr.MethodName) as ColumnBindingConvert;
                value = customDelegate.Invoke(SourceValue);
            }
            else
            {
                value = Convert.ChangeType(SourceValue, targetType);
            }
            return value;
        }

        private void runRecusion(object returnObj)
        {
            if (_bindingType.RecusionMode == RecusionType.Recusion)
            {
                if (returnObj != null && returnObj.GetType().IsSubclassOf(typeof(DataDriven)))
                {
                    (returnObj as DataDriven).DataBinding();
                }
            }
        }
    }

    public class SetPropertyArgs : EventArgs
    {
        public PropertyInfo Property { get; set; }

        public object Value { get; set; }

        public OrderAttribute Attribute { get; set; }

        public SetPropertyArgs(PropertyInfo Prop, Object value, OrderAttribute Attribute)
        {
            this.Property = Prop;
            this.Value = value;
            this.Attribute = Attribute;
        }

        public SetPropertyArgs() { }
    }


}

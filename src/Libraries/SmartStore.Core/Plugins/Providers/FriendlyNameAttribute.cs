using System;

namespace SmartStore.Core.Plugins
{
	[AttributeUsage(AttributeTargets.Class, AllowMultiple=false, Inherited=false)]
	public sealed class FriendlyNameAttribute : Attribute
	{
		public FriendlyNameAttribute(string name)
		{
			Guard.ArgumentNotNull(() => name);

			Name = name;
		}

		public string Name { get; set; }

		public string Description { get; set; }
	}
}

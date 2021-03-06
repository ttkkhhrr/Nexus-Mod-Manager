﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ChinhDo.Transactions;
using Microsoft.Win32;
using Nexus.Client.BackgroundTasks;
using Nexus.Client.Games;
using Nexus.Client.ModManagement;
using Nexus.Client.ModManagement.InstallationLog;
using Nexus.Client.Mods;
using Nexus.Client.PluginManagement;
using Nexus.Client.UI;
using Nexus.Client.Util;
using Nexus.Client.Util.Collections;

namespace Nexus.Client.ModManagement
{
	public class VirtualModActivator : IVirtualModActivator
	{
		[DllImport("kernel32.dll")]
		static extern bool CreateSymbolicLink(string p_strLinkName, string p_strTargetPath, int dwFlags);

		[DllImport("Kernel32.dll", CharSet = CharSet.Unicode)]
		static extern bool CreateHardLink(string p_strLinkName, string p_strTargetPath, IntPtr lpSecurityAttributes);

		public event EventHandler ModActivationChanged;

		#region Static Properties

		private static readonly Version CURRENT_VERSION = new Version("0.2.0.0");
		private static readonly string ACTIVATOR_FILE = "VirtualModConfig.xml";
		private static readonly string ACTIVATOR_INIEDITS = "IniEdits.xml";
		private static readonly string ACTIVATOR_OVERWRITE = "_overwrites";
		public static readonly string ACTIVATOR_FOLDER = "VirtualInstall";
		public static readonly string ACTIVATOR_LINK_FOLDER = "NMMLink";
		public static readonly IMod DummyMod = new InstallLog.DummyMod("ORIGINAL_VALUE", String.Format("Dummy Mod: {0}", "ORIGINAL_VALUE"));

		/// <summary>
		/// Reads the virtual mod activator version from the given config file.
		/// </summary>
		/// /// <param name="p_strVirtualActivatorConfigPath">The config file whose version is to be read.</param>
		/// <returns>The version of the specified config file, or a version of
		/// <c>0.0.0.0</c> if the file format is not recognized.</returns>
		public static Version ReadVersion(string p_strVirtualActivatorConfigPath)
		{
			if (!File.Exists(p_strVirtualActivatorConfigPath))
				return new Version("0.0.0.0");

			XDocument docVirtual = XDocument.Load(p_strVirtualActivatorConfigPath);

			XElement xelVirtual = docVirtual.Element("virtualModActivator");
			if (xelVirtual == null)
				return new Version("0.0.0.0");

			XAttribute xatVersion = xelVirtual.Attribute("fileVersion");
			if (xatVersion == null)
				return new Version("0.0.0.0");

			return new Version(xatVersion.Value);
		}

		/// <summary>
		/// Determines if the config file is valid.
		/// </summary>
		/// <returns><c>true</c> if the config file is valid;
		/// <c>false</c> otherwise.</returns>
		protected static bool IsValid(string p_strVirtualActivatorConfigPath)
		{
			if (!File.Exists(p_strVirtualActivatorConfigPath))
				return false;
			try
			{
				XDocument docVirtual = XDocument.Load(p_strVirtualActivatorConfigPath);
			}
			catch (Exception e)
			{
				Trace.TraceError("Invalid Virtual Mod Activator File ({0}):", p_strVirtualActivatorConfigPath);
				Trace.Indent();
				TraceUtil.TraceException(e);
				Trace.Unindent();
				return false;
			}
			return true;
		}
		#endregion

		private ThreadSafeObservableList<IVirtualModLink> m_tslVirtualModList = new ThreadSafeObservableList<IVirtualModLink>();
		private ThreadSafeObservableList<IVirtualModInfo> m_tslVirtualModInfo = new ThreadSafeObservableList<IVirtualModInfo>();
		private bool m_booInitialized = false;
		private bool m_booDisableLinkCreation = false;
		private bool m_booDisableIniLogging = false;
		private bool m_booForceHardLinks = false;
		private string m_strGameDataPath = String.Empty;
		private string m_strVirtualActivatorPath = String.Empty;
		private string m_strVirtualActivatorConfigPath = String.Empty;
		private string m_strVirtualActivatorIniEditsPath = String.Empty;
		private string m_strVirtualActivatorOverwritePath = String.Empty;

		#region Properties

		/// <summary>
		/// Gets the mod manager to use to manage mods.
		/// </summary>
		/// <value>The mod manager to use to manage mods.</value>
		protected ModManager ModManager { get; private set; }

		/// <summary>
		/// Gets the plugin manager to use to manage plugins.
		/// </summary>
		/// <value>The plugin manager to use to manage plugins.</value>
		protected IPluginManager PluginManager { get; private set; }

		/// <summary>
		/// Gets the current game mode.
		/// </summary>
		/// <value>The current game mode.</value>
		public IGameMode GameMode { get; private set; }

		/// <summary>
		/// Gets the install log that tracks mod install info
		/// for the current game mode.
		/// </summary>
		/// <value>The install log that tracks mod install info
		/// for the current game mode.</value>
		protected IInstallLog ModInstallLog { get; private set; }

		/// <summary>
		/// Gets the application's envrionment info.
		/// </summary>
		/// <value>The application's envrionment info.</value>
		protected IEnvironmentInfo EnvironmentInfo { get; private set; }

		/// <summary>
		/// Gets the current game mode's VirtualInstall folder.
		/// </summary>
		/// <value>The current game mode's VirtualInstall folder.</value>
		public string VirtualFoder
		{
			get
			{
				if (EnvironmentInfo != null)
					if (!String.IsNullOrEmpty(EnvironmentInfo.Settings.VirtualFolder[GameMode.ModeId]))
					{
						string strVirtual = EnvironmentInfo.Settings.VirtualFolder[GameMode.ModeId];
						return Path.Combine(strVirtual, ACTIVATOR_FOLDER);
					}

				return String.Empty;
			}
		}

		/// <summary>
		/// Gets the current game mode's NMMLink folder.
		/// </summary>
		/// <value>The current game mode's NMMLink folder.</value>
		public string HDLinkFolder
		{
			get
			{
				if (EnvironmentInfo != null)
					if (!String.IsNullOrWhiteSpace(EnvironmentInfo.Settings.HDLinkFolder[GameMode.ModeId]))
					{
						string strLink = EnvironmentInfo.Settings.HDLinkFolder[GameMode.ModeId];
						if (!String.IsNullOrWhiteSpace(strLink))
							return Path.Combine(strLink, GameMode.ModeId, ACTIVATOR_LINK_FOLDER);
						else
							return String.Empty;
					}

				if (MultiHDMode == true)
					throw new ArgumentNullException("It seems the MultiHD mode is enabled but the program is unable to retrieve the Link folder.");
				else
					return String.Empty;
			}
		}

		/// <summary>
		/// Gets the current support version of the virtual mod activator.
		/// </summary>
		/// <value>The current support version of the virtual mod activator.</value>
		public static Version CurrentVersion
		{
			get
			{
				return CURRENT_VERSION;
			}
		}

		/// <summary>
		/// Gets whether the MultiHD mode is currently enabled.
		/// </summary>
		/// <value>Whether the MultiHD mode is currently enabled.</value>
		public bool MultiHDMode 
		{
			get
			{
				return m_booForceHardLinks;
			}
		}

		/// <summary>
		/// Gets whether the VirtualModActivator is initialized.
		/// </summary>
		/// <value>Whether the VirtualModActivator is initialized.</value>
		public bool Initialized
		{
			get
			{
				return m_booInitialized;
			}
		}

		/// <summary>
		/// Gets whether link creation is disabled.
		/// </summary>
		/// <value>Whether link creation is disabled.</value>
		public bool DisableLinkCreation
		{
			get
			{
				return m_booDisableLinkCreation;
			}
			set
			{
				m_booDisableLinkCreation = value;
			}
		}

		/// <summary>
		/// Gets whether ini logging is disabled.
		/// </summary>
		/// <value>Whether ini logging is disabled.</value>
		public bool DisableIniLogging
		{
			get
			{
				return m_booDisableIniLogging;
			}
		}

		public IEnumerable<string> ActiveModList
		{
			get
			{
				return VirtualMods.Select(x => x.ModFileName.ToLowerInvariant());
			}
		}

		public string VirtualPath
		{
			get
			{
				return m_strVirtualActivatorPath;
			}
		}

		public ThreadSafeObservableList<IVirtualModLink> VirtualLinks
		{
			get
			{
				return m_tslVirtualModList;
			}
		}

		public ThreadSafeObservableList<IVirtualModInfo> VirtualMods
		{
			get
			{
				return m_tslVirtualModInfo;
			}
		}

		public Int32 ModCount
		{
			get
			{
				return m_tslVirtualModInfo.Count;
			}
		}

		protected string NewVirtualFolder;
		protected string NewLinkFolder;
		protected bool NewMultiHD;

		#endregion

		#region Constructors

		public VirtualModActivator(ModManager p_mmgModManager, IPluginManager p_pmgPluginManager, IGameMode p_gmdGameMode, IInstallLog p_ilgModInstallLog, IEnvironmentInfo p_eifEnvironmentInfo, string p_strModFolder)
		{
			ModManager = p_mmgModManager;
			PluginManager = p_pmgPluginManager;
			GameMode = p_gmdGameMode;
			ModInstallLog = p_ilgModInstallLog;
			EnvironmentInfo = p_eifEnvironmentInfo;
			m_booForceHardLinks = EnvironmentInfo.Settings.MultiHDInstall[GameMode.ModeId];
			string strVirtualFolder = EnvironmentInfo.Settings.VirtualFolder[GameMode.ModeId];
			m_strVirtualActivatorPath = Path.Combine(strVirtualFolder, ACTIVATOR_FOLDER);
			m_strGameDataPath = GameMode.UsesPlugins ? GameMode.PluginDirectory : GameMode.InstallationPath;
			m_strVirtualActivatorConfigPath = Path.Combine(m_strVirtualActivatorPath, ACTIVATOR_FILE);
			m_strVirtualActivatorIniEditsPath = Path.Combine(m_strVirtualActivatorPath, ACTIVATOR_INIEDITS);
			m_strVirtualActivatorOverwritePath = Path.Combine(m_strVirtualActivatorPath, ACTIVATOR_OVERWRITE);
			if (!Directory.Exists(m_strVirtualActivatorPath))
				Directory.CreateDirectory(m_strVirtualActivatorPath);

			if (!String.IsNullOrEmpty(EnvironmentInfo.Settings.HDLinkFolder[GameMode.ModeId]))
			{
				string strHDLink = Path.Combine(Path.Combine(EnvironmentInfo.Settings.HDLinkFolder[GameMode.ModeId], GameMode.ModeId), ACTIVATOR_LINK_FOLDER);

				if (!Directory.Exists(strHDLink))
					Directory.CreateDirectory(strHDLink);
			}

			if (!Directory.Exists(m_strVirtualActivatorOverwritePath))
				Directory.CreateDirectory(m_strVirtualActivatorOverwritePath);
		}

		#endregion

		#region Virtual Mod Activator

		#region List Management

		public void Initialize()
		{
			if (IsValid(m_strVirtualActivatorConfigPath))
				if (ReadVersion(m_strVirtualActivatorConfigPath) == CURRENT_VERSION)
				{
					SetCurrentList(LoadList(m_strVirtualActivatorConfigPath));
					m_booInitialized = true;
				}
		}

		public void Setup()
		{
			SaveList();
			SetCurrentList(LoadList(m_strVirtualActivatorConfigPath));
			m_booInitialized = true;
		}

		public void Reset()
		{
			if (!String.IsNullOrEmpty(NewVirtualFolder))
			{
				m_strVirtualActivatorPath = Path.Combine(NewVirtualFolder, ACTIVATOR_FOLDER);
				m_strVirtualActivatorConfigPath = Path.Combine(m_strVirtualActivatorPath, ACTIVATOR_FILE);
				m_strVirtualActivatorIniEditsPath = Path.Combine(m_strVirtualActivatorPath, ACTIVATOR_INIEDITS);
				if (!Directory.Exists(m_strVirtualActivatorPath))
					Directory.CreateDirectory(m_strVirtualActivatorPath);
			}

			m_booForceHardLinks = NewMultiHD;

			if (!String.IsNullOrEmpty(NewLinkFolder))
			{
				if (!String.IsNullOrEmpty(HDLinkFolder))
					FileUtil.ForceDelete(HDLinkFolder);

				string strHDLink = Path.Combine(NewLinkFolder, GameMode.ModeId, ACTIVATOR_LINK_FOLDER);

				if (!Directory.Exists(strHDLink))
					Directory.CreateDirectory(strHDLink);
			}
			else if (String.IsNullOrEmpty(NewLinkFolder) && !String.IsNullOrEmpty(HDLinkFolder))
			{
				if (Directory.Exists(HDLinkFolder))
					FileUtil.ForceDelete(HDLinkFolder);
			}

			if (!String.IsNullOrWhiteSpace(NewVirtualFolder))
			{
				if (!String.Equals(EnvironmentInfo.Settings.VirtualFolder[GameMode.ModeId], NewVirtualFolder))
				{
					EnvironmentInfo.Settings.VirtualFolder[GameMode.ModeId] = NewVirtualFolder;
				}
			}
			if (!String.Equals(EnvironmentInfo.Settings.HDLinkFolder[GameMode.ModeId], NewLinkFolder))
			{
				EnvironmentInfo.Settings.HDLinkFolder[GameMode.ModeId] = NewLinkFolder;
			}
			if (NewMultiHD == !EnvironmentInfo.Settings.MultiHDInstall[GameMode.ModeId])
			{
				EnvironmentInfo.Settings.MultiHDInstall[GameMode.ModeId] = NewMultiHD;
			}

			SaveRegistry(GameMode.ModeId, NewVirtualFolder, NewLinkFolder, NewMultiHD);

			EnvironmentInfo.Settings.Save();
		}

		public void SaveList()
		{
			if (!Directory.Exists(m_strVirtualActivatorPath))
				Directory.CreateDirectory(m_strVirtualActivatorPath);
			SaveModList();
		}

		private void SaveModList()
		{
			SaveModList(m_strVirtualActivatorConfigPath);
		}

		public void SaveModList(string p_strPath)
		{
			XDocument docVirtual = new XDocument();
			XElement xelRoot = new XElement("virtualModActivator", new XAttribute("fileVersion", CURRENT_VERSION));
			docVirtual.Add(xelRoot);
			
			XElement xelModList = new XElement("modList");
			xelRoot.Add(xelModList);
			xelModList.Add(from mod in m_tslVirtualModInfo
						   select new XElement("modInfo",
							   new XAttribute("modId", mod.ModId ?? String.Empty),
							   new XAttribute("modName", mod.ModName),
							   new XAttribute("modFileName", mod.ModFileName),
							   new XAttribute("modFilePath", mod.ModFilePath),
							   from link in m_tslVirtualModList.Where(x => x.ModInfo == mod)
							   select new XElement("fileLink",
								   new XAttribute("realPath", link.RealModPath),
								   new XAttribute("virtualPath", link.VirtualModPath),
								   new XElement("linkPriority",
									   new XText(link.Priority.ToString())),
								   new XElement("isActive",
									   new XText(link.Active.ToString())))));

			docVirtual.Save(p_strPath);
		}

		private void SaveModList(string p_strPath, List<IVirtualModInfo> p_lstVirtualModInfo, List<IVirtualModLink> p_lstVirtualModList)
		{
			XDocument docVirtual = new XDocument();
			XElement xelRoot = new XElement("virtualModActivator", new XAttribute("fileVersion", CURRENT_VERSION));
			docVirtual.Add(xelRoot);

			XElement xelModList = new XElement("modList");
			xelRoot.Add(xelModList);
			xelModList.Add(from mod in p_lstVirtualModInfo
						   select new XElement("modInfo",
							   new XAttribute("modId", mod.ModId ?? String.Empty),
							   new XAttribute("modName", mod.ModName),
							   new XAttribute("modFileName", mod.ModFileName),
							   new XAttribute("modFilePath", mod.ModFilePath),
							   from link in p_lstVirtualModList.Where(x => x.ModInfo == mod)
							   select new XElement("fileLink",
								   new XAttribute("realPath", link.RealModPath),
								   new XAttribute("virtualPath", link.VirtualModPath),
								   new XElement("linkPriority",
									   new XText(link.Priority.ToString())),
								   new XElement("isActive",
									   new XText(link.Active.ToString())))));

			docVirtual.Save(p_strPath);
		}

		public void SetCurrentList(IList<IVirtualModLink> p_ilvVirtualLinks)
		{
			m_tslVirtualModList.Clear();
			m_tslVirtualModList = new ThreadSafeObservableList<IVirtualModLink>(p_ilvVirtualLinks);
		}

		public List<IVirtualModLink> LoadList(string p_strXMLFilePath)
		{
			List<IVirtualModLink> lstVirtualLinks = new List<IVirtualModLink>();
			List<IVirtualModInfo> lstVirtualMods = new List<IVirtualModInfo>();

			if (File.Exists(p_strXMLFilePath))
			{
				XDocument docVirtual = XDocument.Load(p_strXMLFilePath);
				string strVersion = docVirtual.Element("virtualModActivator").Attribute("fileVersion").Value;
				if (!(CURRENT_VERSION.ToString() == strVersion))
					throw new Exception(String.Format("Invalid Virtual Mod Activator version: {0} Expecting {1}", strVersion, CURRENT_VERSION));

				try
				{
					XElement xelModList = docVirtual.Descendants("modList").FirstOrDefault();
					if ((xelModList != null) && xelModList.HasElements)
					{
						foreach (XElement xelMod in xelModList.Elements("modInfo"))
						{
							string strModId = xelMod.Attribute("modId").Value;
							string strModName = xelMod.Attribute("modName").Value;
							string strModFileName = xelMod.Attribute("modFileName").Value;
							string strModFilePath = xelMod.Attribute("modFilePath").Value;

							VirtualModInfo vmiMod = new VirtualModInfo(strModId, strModName, strModFileName, strModFilePath);
							lstVirtualMods.Add(vmiMod);

							foreach (XElement xelLink in xelMod.Elements("fileLink"))
							{
								string strRealPath = xelLink.Attribute("realPath").Value;
								string strVirtualPath = xelLink.Attribute("virtualPath").Value;
								Int32 intPriority = 0;
								try
								{
									intPriority = Convert.ToInt32(xelLink.Element("linkPriority").Value);
								}
								catch { }
								bool booActive = false;
								try
								{
									booActive = Convert.ToBoolean(xelLink.Element("isActive").Value);
								}
								catch { }

								lstVirtualLinks.Add(new VirtualModLink(strRealPath, strVirtualPath, intPriority, booActive, vmiMod));
							}
						}
					}
				}
				catch { }
			}
			m_tslVirtualModInfo.Clear();
			m_tslVirtualModInfo = new ThreadSafeObservableList<IVirtualModInfo>(lstVirtualMods);
			return lstVirtualLinks;
		}

		public bool LoadListOnDemand(string p_strProfilePath, out List<IVirtualModLink> p_lstVirtualLinks, out List<IVirtualModInfo> p_lstVirtualMods)
		{
			p_lstVirtualLinks = new List<IVirtualModLink>();
			p_lstVirtualMods = new List<IVirtualModInfo>();

			if (File.Exists(p_strProfilePath))
			{
				XDocument docVirtual = XDocument.Load(p_strProfilePath);
				string strVersion = docVirtual.Element("virtualModActivator").Attribute("fileVersion").Value;
				if (!(CURRENT_VERSION.ToString() == strVersion))
					throw new Exception(String.Format("Invalid Virtual Mod Activator version: {0} Expecting {1}", strVersion, CURRENT_VERSION));

				try
				{
					XElement xelModList = docVirtual.Descendants("modList").FirstOrDefault();
					if ((xelModList != null) && xelModList.HasElements)
					{
						foreach (XElement xelMod in xelModList.Elements("modInfo"))
						{
							string strModId = xelMod.Attribute("modId").Value;
							string strModName = xelMod.Attribute("modName").Value;
							string strModFileName = xelMod.Attribute("modFileName").Value;
							string strModFilePath = xelMod.Attribute("modFilePath").Value;

							VirtualModInfo vmiMod = new VirtualModInfo(strModId, strModName, strModFileName, strModFilePath);
							p_lstVirtualMods.Add(vmiMod);

							foreach (XElement xelLink in xelMod.Elements("fileLink"))
							{
								string strRealPath = xelLink.Attribute("realPath").Value;
								string strVirtualPath = xelLink.Attribute("virtualPath").Value;
								Int32 intPriority = 0;
								try
								{
									intPriority = Convert.ToInt32(xelLink.Element("linkPriority").Value);
								}
								catch { }
								bool booActive = false;
								try
								{
									booActive = Convert.ToBoolean(xelLink.Element("isActive").Value);
								}
								catch { }

								p_lstVirtualLinks.Add(new VirtualModLink(strRealPath, strVirtualPath, intPriority, booActive, vmiMod));
							}
						}

						return true;
					}
				}
				catch { }
			}

			return false;
		}

		public List<IVirtualModLink> LoadImportedList(string p_strXML)
		{
			List<IVirtualModLink> lstVirtualLinks = new List<IVirtualModLink>();
			List<IVirtualModInfo> lstVirtualMods = new List<IVirtualModInfo>();

			XDocument docVirtual = XDocument.Parse(p_strXML);
			string strVersion = docVirtual.Element("virtualModActivator").Attribute("fileVersion").Value;
			if (!(CURRENT_VERSION.ToString() == strVersion))
				throw new Exception(String.Format("Invalid Virtual Mod Activator version: {0} Expecting {1}", strVersion, CURRENT_VERSION));

			try
			{
				XElement xelModList = docVirtual.Descendants("modList").FirstOrDefault();
				if ((xelModList != null) && xelModList.HasElements)
				{
					foreach (XElement xelMod in xelModList.Elements("modInfo"))
					{
						string strModId = xelMod.Attribute("modId").Value;
						string strModName = xelMod.Attribute("modName").Value;
						string strModFileName = xelMod.Attribute("modFileName").Value;
						string strModFilePath = xelMod.Attribute("modFilePath").Value;

						VirtualModInfo vmiMod = new VirtualModInfo(strModId, strModName, strModFileName, strModFilePath);
						lstVirtualMods.Add(vmiMod);

						foreach (XElement xelLink in xelMod.Elements("fileLink"))
						{
							string strRealPath = xelLink.Attribute("realPath").Value;
							string strVirtualPath = xelLink.Attribute("virtualPath").Value;
							Int32 intPriority = 0;
							try
							{
								intPriority = Convert.ToInt32(xelLink.Element("linkPriority").Value);
							}
							catch { }
							bool booActive = false;
							try
							{
								booActive = Convert.ToBoolean(xelLink.Element("isActive").Value);
							}
							catch { }

							lstVirtualLinks.Add(new VirtualModLink(strRealPath, strVirtualPath, intPriority, booActive, vmiMod));
						}
					}
				}
			}
			catch { }

			return lstVirtualLinks;
		}

		#endregion

		#region Link Management

		public string CheckVirtualLink(string p_strFilePath)
		{
			string strPath = p_strFilePath;
			IVirtualModLink ivlVirtualModLink = VirtualLinks.Find(x => Path.Combine(m_strVirtualActivatorPath, x.RealModPath) == p_strFilePath);
			if (ivlVirtualModLink != null)
			{ 
				strPath = Path.Combine(m_strGameDataPath, ivlVirtualModLink.VirtualModPath);
			}

			return strPath;
		}

		public Int32 CheckFileLink(string p_strFilePath, out IMod p_modMod, out List<IVirtualModLink> p_lstFileLinks)
		{
			return CheckFileLink(p_strFilePath, -1, out p_modMod, out p_lstFileLinks);
		}

		private Int32 CheckFileLink(string p_strFilePath, Int32 p_intCurrentPriority, out IMod p_modMod)
		{
			List<IVirtualModLink> lstDummy;
			return CheckFileLink(p_strFilePath, p_intCurrentPriority, out p_modMod, out lstDummy);
		}

		private Int32 CheckFileLink(string p_strFilePath, Int32 p_intCurrentPriority, out IMod p_modMod, out List<IVirtualModLink> p_lstFileLinks)
		{
			Int32 intPriority = -1;
			p_modMod = null;

			List<IVirtualModLink> lstVirtualModLink = new List<IVirtualModLink>();
			if (p_intCurrentPriority >= 0)
				lstVirtualModLink = VirtualLinks.Where(x => (x.VirtualModPath.ToLowerInvariant() == p_strFilePath.ToLowerInvariant()) && (x.Priority != p_intCurrentPriority)).ToList();
			else
				lstVirtualModLink = VirtualLinks.Where(x => x.VirtualModPath.ToLowerInvariant() == p_strFilePath.ToLowerInvariant()).ToList();

			if ((lstVirtualModLink != null) && (lstVirtualModLink.Count > 0))
			{
				IVirtualModLink ivlModLink = lstVirtualModLink.OrderByDescending(x => x.Priority).FirstOrDefault();
				if (ivlModLink != null)
					intPriority = ivlModLink.Priority;
				ivlModLink = lstVirtualModLink.OrderBy(x => x.Priority).FirstOrDefault();
				if (ivlModLink != null)
					p_modMod = ModManager.ManagedMods.FirstOrDefault(x => String.Equals(Path.GetFileName(x.Filename), ivlModLink.ModInfo.ModFileName, StringComparison.InvariantCultureIgnoreCase));
			}
			else if (File.Exists(p_strFilePath) && (p_intCurrentPriority >= 0))
				intPriority = 0;
			else if (p_intCurrentPriority == -1)
			{
				string strLoosePath = Path.Combine(m_strGameDataPath, p_strFilePath);
				if (File.Exists(strLoosePath))
					p_modMod = DummyMod;
			}

			p_lstFileLinks = lstVirtualModLink;

			return intPriority;
		}
		
		public bool PurgeLinks()
		{
			if (m_tslVirtualModInfo.Count > 0)
			{
				foreach (IVirtualModInfo modInfo in m_tslVirtualModInfo)
				{
					IMod modMod = ModManager.ManagedMods.FirstOrDefault(x => Path.GetFileName(x.Filename).ToLowerInvariant() == modInfo.ModFileName.ToLowerInvariant());
					DisableMod(modMod, true);
				}

				VirtualLinks.Clear();
				SaveList();
			}
			return true;
		}

		public bool PurgeLinks(IList<IVirtualModLink> p_lstToPurge)
		{
			if (p_lstToPurge.Count > 0)
			{
				foreach (IVirtualModLink modLink in p_lstToPurge)
				{
					IMod modMod = ModManager.ManagedMods.FirstOrDefault(x => modLink.ModInfo.ModFileName.Equals(Path.GetFileName(x.Filename).ToString(), StringComparison.InvariantCultureIgnoreCase));
					RemoveFileLink(modLink, modMod, true);
				}
				
				List<IVirtualModInfo> lstModInfo = m_tslVirtualModList.Select(x => x.ModInfo).Distinct().ToList();
				List<IVirtualModInfo> lstMissing = m_tslVirtualModInfo.Except(lstModInfo, new VirtualModInfoEqualityComparer()).ToList();
				if ((lstMissing != null) && (lstMissing.Count > 0))
					m_tslVirtualModInfo.RemoveRange(lstMissing);
				
				SaveList();
			}
			return true;
		}

		public void AddInactiveLink(IMod p_modMod, string p_strBaseFilePath, Int32 p_intPriority)
		{
			IVirtualModInfo modInfo = m_tslVirtualModInfo.Where(x => x.ModFileName.ToLowerInvariant() == Path.GetFileName(p_modMod.Filename).ToLowerInvariant()).FirstOrDefault();
			if (modInfo == null)
			{
				VirtualModInfo vmiModInfo = new VirtualModInfo(p_modMod.Id, p_modMod.ModName, p_modMod.Filename);
				m_tslVirtualModInfo.Add(vmiModInfo);
				modInfo = vmiModInfo;
			}
			string strRealFilePath = Path.Combine(Path.GetFileNameWithoutExtension(p_modMod.Filename), p_strBaseFilePath);
			m_tslVirtualModList.Add(new VirtualModLink(strRealFilePath, p_strBaseFilePath, p_intPriority, false, modInfo));
		}

		public string AddFileLink(IMod p_modMod, string p_strBaseFilePath, bool p_booIsSwitching, bool p_booIsRestoring, Int32 p_intPriority)
		{
			if (p_booIsSwitching)
			{
				string strLoosePath = Path.Combine(m_strGameDataPath, p_strBaseFilePath);
				if (File.Exists(strLoosePath))
					OverwriteLooseFile(p_strBaseFilePath, Path.GetFileName(p_modMod.Filename));
			}
			return AddFileLink(p_modMod, p_strBaseFilePath, null, p_booIsSwitching, p_booIsRestoring, false, p_intPriority);
		}

		public string AddFileLink(IMod p_modMod, string p_strBaseFilePath, string p_strSourceFile, bool p_booIsSwitching, bool p_booIsRestoring, bool p_booHandlePlugin, Int32 p_intPriority)
		{
			string strSourceFile = p_strSourceFile;
			string strRealFilePath = Path.Combine(Path.GetFileNameWithoutExtension(p_modMod.Filename), p_strBaseFilePath);
			string strAdjustedFilePath = GameMode.GetModFormatAdjustedPath(p_modMod.Format, p_strBaseFilePath, p_modMod, true);
			string strVirtualFileLink = String.Empty;

			if (GameMode.HasSecondaryInstallPath && GameMode.CheckSecondaryInstall(p_modMod))
				strVirtualFileLink = Path.Combine(GameMode.SecondaryInstallationPath, strAdjustedFilePath);
			else
				strVirtualFileLink = Path.Combine(m_strGameDataPath, strAdjustedFilePath);

			string strActivatorFilePath = String.IsNullOrWhiteSpace(strSourceFile) ? Path.Combine(m_strVirtualActivatorPath, strRealFilePath) : strSourceFile;
			if (!File.Exists(strActivatorFilePath))
			{
				strRealFilePath = Path.Combine(Path.GetFileNameWithoutExtension(p_modMod.Filename), GameMode.GetModFormatAdjustedPath(p_modMod.Format, p_strBaseFilePath, p_modMod, false));
				strActivatorFilePath = Path.Combine(m_strVirtualActivatorPath, strRealFilePath);
				if (!File.Exists(strActivatorFilePath))
				{
					strRealFilePath = Path.Combine(Path.GetFileNameWithoutExtension(p_modMod.Filename), GameMode.GetModFormatAdjustedPath(p_modMod.Format, p_strBaseFilePath, p_modMod, true));
					strActivatorFilePath = Path.Combine(m_strVirtualActivatorPath, strRealFilePath);
				}
			}

			string strLinkFilePath = String.Empty;
			if (MultiHDMode)
			{
				strLinkFilePath = String.IsNullOrWhiteSpace(strSourceFile) ? Path.Combine(HDLinkFolder, strRealFilePath) : strSourceFile;
				if (!File.Exists(strLinkFilePath))
				{
					strRealFilePath = Path.Combine(Path.GetFileNameWithoutExtension(p_modMod.Filename), GameMode.GetModFormatAdjustedPath(p_modMod.Format, p_strBaseFilePath, p_modMod, true));
					strLinkFilePath = Path.Combine(HDLinkFolder, strRealFilePath);
					if (!File.Exists(strLinkFilePath))
					{
						strRealFilePath = Path.Combine(Path.GetFileNameWithoutExtension(p_modMod.Filename), GameMode.GetModFormatAdjustedPath(p_modMod.Format, p_strBaseFilePath, p_modMod, false));
						strLinkFilePath = Path.Combine(HDLinkFolder, strRealFilePath);
					}
				}
			}

			if (!Directory.Exists(Path.GetDirectoryName(strVirtualFileLink)))
				FileUtil.CreateDirectory(Path.GetDirectoryName(strVirtualFileLink));

			string strFileType = Path.GetExtension(strVirtualFileLink);
			if (!strFileType.StartsWith("."))
				strFileType = "." + strFileType;

			if (File.Exists(strVirtualFileLink))
				FileUtil.ForceDelete(strVirtualFileLink);

			IVirtualModInfo modInfo = m_tslVirtualModInfo.Where(x => x.ModFileName.ToLowerInvariant() == Path.GetFileName(p_modMod.Filename).ToLowerInvariant()).FirstOrDefault();
			if (modInfo == null)
			{
				VirtualModInfo vmiModInfo = new VirtualModInfo(p_modMod.Id, p_modMod.ModName, p_modMod.Filename);
				m_tslVirtualModInfo.Add(vmiModInfo);
				modInfo = vmiModInfo;
			}

			if (strFileType.Equals(".exe", StringComparison.InvariantCultureIgnoreCase) || strFileType.Equals(".jar", StringComparison.InvariantCultureIgnoreCase))
			{
				File.Copy(MultiHDMode ?  strLinkFilePath : strActivatorFilePath, strVirtualFileLink, true);

				if (File.Exists(strVirtualFileLink))
				{
					if (!p_booIsRestoring)
						m_tslVirtualModList.Add(new VirtualModLink(strRealFilePath, p_strBaseFilePath, p_intPriority, true, modInfo));
					else
						strVirtualFileLink = String.Empty;
				}
				else
					strVirtualFileLink = String.Empty;
			}
			else if (GameMode.HardlinkRequiredFilesType(strVirtualFileLink))
			{
				if (MultiHDMode)
				{
					bool booSuccess = CreateHardLink(strVirtualFileLink, strLinkFilePath, IntPtr.Zero);
					if (!booSuccess)
						File.Copy(strLinkFilePath, strVirtualFileLink, true);

					if (booSuccess || File.Exists(strVirtualFileLink))
					{
						if (!p_booIsRestoring)
							m_tslVirtualModList.Add(new VirtualModLink(strRealFilePath, p_strBaseFilePath, p_intPriority, true, modInfo));
						else
							strVirtualFileLink = String.Empty;
					}
					else
						strVirtualFileLink = String.Empty;
				}
				else
				{
					bool booSuccess = CreateHardLink(strVirtualFileLink, strActivatorFilePath, IntPtr.Zero);
					if (!booSuccess)
						File.Copy(strActivatorFilePath, strVirtualFileLink, true);

					if (booSuccess || File.Exists(strVirtualFileLink))
					{
						if (!p_booIsRestoring)
							m_tslVirtualModList.Add(new VirtualModLink(strRealFilePath, p_strBaseFilePath, p_intPriority, true, modInfo));
						else
							strVirtualFileLink = String.Empty;
					}
					else
						strVirtualFileLink = String.Empty;
				}
			}
			else if (!DisableLinkCreation)
			{
				if (!MultiHDMode && (CreateHardLink(strVirtualFileLink, strActivatorFilePath, IntPtr.Zero)))
				{
					if (!p_booIsRestoring)
						m_tslVirtualModList.Add(new VirtualModLink(strRealFilePath, p_strBaseFilePath, p_intPriority, true, modInfo));
					else
						strVirtualFileLink = String.Empty;
				}
				else if (CreateSymbolicLink(strVirtualFileLink, strActivatorFilePath, 0))
				{
					if (!p_booIsRestoring)
						m_tslVirtualModList.Add(new VirtualModLink(strRealFilePath, p_strBaseFilePath, p_intPriority, true, modInfo));
					else
						strVirtualFileLink = String.Empty;
				}
				else
					strVirtualFileLink = String.Empty;
			}
			else
				strVirtualFileLink = String.Empty;

			if (p_booIsSwitching && (PluginManager != null) && !String.IsNullOrEmpty(strVirtualFileLink) && !p_booIsRestoring)
				if (PluginManager.IsActivatiblePluginFile(strVirtualFileLink))
				{
					PluginManager.AddPlugin(strVirtualFileLink);
					if (p_booHandlePlugin)
						PluginManager.ActivatePlugin(strVirtualFileLink);
				}
			
			return strVirtualFileLink;
		}

		public void RemoveFileLink(string p_strFilePath, IMod p_modMod)
		{
			string strPathCheck = p_strFilePath.Replace(m_strVirtualActivatorPath + Path.DirectorySeparatorChar.ToString(), String.Empty);
			IVirtualModLink ivlVirtualModLink = VirtualLinks.Find(x => x.VirtualModPath == strPathCheck);
			if (ivlVirtualModLink == null)
				ivlVirtualModLink = VirtualLinks.Find(x => x.RealModPath == strPathCheck);
			if (ivlVirtualModLink == null)
				ivlVirtualModLink = VirtualLinks.Find(x => Path.GetFullPath(x.RealModPath) == Path.GetFullPath(strPathCheck));
			RemoveFileLink(ivlVirtualModLink, p_modMod, false);
		}

		public void RemoveFileLink(IVirtualModLink p_ivlVirtualLink, IMod p_modMod)
		{
			RemoveFileLink(p_ivlVirtualLink, p_modMod, false);
		}

		protected void RemoveFileLink(IVirtualModLink p_ivlVirtualLink, IMod p_modMod, bool p_booPurging)
		{
			IMod modCheck = null;
			
			if (p_ivlVirtualLink != null)
			{
				bool booActive = p_ivlVirtualLink.Active;
				int intCurrentPriority = p_ivlVirtualLink.Priority;
				List<IVirtualModLink> lstOverwrites = null;
				Int32 intPriority = CheckFileLink(p_ivlVirtualLink.VirtualModPath, intCurrentPriority, out modCheck, out lstOverwrites);
				string strLinkPath = Path.Combine(m_strGameDataPath, p_ivlVirtualLink.VirtualModPath);
				if ((!File.Exists(strLinkPath)) && (p_modMod != null))
					strLinkPath = Path.Combine(m_strGameDataPath, GameMode.GetModFormatAdjustedPath(p_modMod.Format, p_ivlVirtualLink.VirtualModPath, true));

				if (GameMode.HasSecondaryInstallPath)
					if (GameMode.CheckSecondaryUninstall(strLinkPath))
						return;

				if ((PluginManager != null) && ((intPriority < 0) || (modCheck == null)))
				{
					if (PluginManager.IsActivatiblePluginFile(strLinkPath))
					{
						var plugin = PluginManager.ActivePlugins.Where(x => x.Filename == strLinkPath);

						if (plugin != null)
						{
							PluginManager.DeactivatePlugin(strLinkPath);
							PluginManager.RemovePlugin(strLinkPath);
						}
					}
				}

				string strPath = string.Empty;
				string strStop = m_strGameDataPath;
				if ((p_modMod != null) && (GameMode.HasSecondaryInstallPath && GameMode.CheckSecondaryInstall(p_modMod)))
				{
					strPath = Path.Combine(GameMode.SecondaryInstallationPath, GameMode.GetModFormatAdjustedPath(p_modMod.Format, p_ivlVirtualLink.VirtualModPath, p_modMod, true));
					strStop = GameMode.SecondaryInstallationPath;
				}
				else
					strPath = strLinkPath;

				if (p_ivlVirtualLink.Active)
					if (File.Exists(strPath))
						FileUtil.ForceDelete(strPath);

				VirtualLinks.Remove(p_ivlVirtualLink);

				if ((intPriority >= 0) && !p_booPurging && (modCheck != null))
				{
					if (booActive)
						UpdateLinkListPriority(lstOverwrites, modCheck, false, true);
				}
				else
				{
					if (Directory.Exists(m_strVirtualActivatorOverwritePath))
					{
						string strOverwrite = Path.Combine(m_strVirtualActivatorOverwritePath, Path.GetFileNameWithoutExtension(p_ivlVirtualLink.ModInfo.ModFileName), p_ivlVirtualLink.VirtualModPath);
						if (File.Exists(strOverwrite))
						{
							try
							{
								File.Move(strOverwrite, strPath);
								TrimEmptyDirectories(Path.GetDirectoryName(strOverwrite), m_strVirtualActivatorOverwritePath);
							}
							catch { }
						}
					}
				}

				TrimEmptyDirectories(Path.GetDirectoryName(strPath), strStop);
			}
		}

		public void UpdateLinkPriority(IVirtualModLink p_ivlFileLink)
		{
			VirtualModLink vmlUpdated = new VirtualModLink(p_ivlFileLink);
			m_tslVirtualModList.Remove(p_ivlFileLink);

			vmlUpdated.Priority = 0;
			vmlUpdated.Active = true;
			m_tslVirtualModList.Add(vmlUpdated);
		}

		private void UpdateLinkListPriority(List<IVirtualModLink> p_lstFileLinks, IMod p_modMod, bool p_booIncrement, bool p_booActivateFirst)
		{
			m_tslVirtualModList.RemoveRange(p_lstFileLinks);

			if (p_booActivateFirst)
			{
				VirtualModLink vmlFirst = new VirtualModLink(p_lstFileLinks.OrderBy(x => x.Priority).First());
				p_lstFileLinks.Remove(vmlFirst);

				if (vmlFirst.Priority > 0)
					vmlFirst.Priority--;
				vmlFirst.Active = true;
				m_tslVirtualModList.Add(vmlFirst);
				AddFileLink(p_modMod, vmlFirst.VirtualModPath, false, true, vmlFirst.Priority);
			}

			if (p_lstFileLinks.Count > 0)
			{
				foreach (VirtualModLink vml in p_lstFileLinks.OrderBy(x => x.Priority))
				{
					if (p_booIncrement)
						vml.Priority++;
					else
						if (vml.Priority > 0)
							vml.Priority--;

					vml.Active = false;
					m_tslVirtualModList.Add(vml);
				}
			}
		}

		public void UpdateLinkListPriority(List<IVirtualModLink> p_lstFileLinks)
		{
			UpdateLinkListPriority(p_lstFileLinks, null, true, false);
		}

		#endregion

		#region Mod Management

		public void DisableMod(IMod p_modMod)
		{
			DisableMod(p_modMod, false);
		}

		public void DisableModFiles(IMod p_modMod)
		{
			DisableMod(p_modMod, true);
		}

		private void DisableMod(IMod p_modMod, bool p_booPurging)
		{
			if (CheckIsModActive(p_modMod))
			{
				if (p_modMod != null)
				{
					if (m_tslVirtualModList.Count > 0)
					{
						List<IVirtualModLink> ivlLinks = m_tslVirtualModList.Where(x => (x.ModInfo != null) && (x.ModInfo.ModFileName.ToLowerInvariant() == Path.GetFileName(p_modMod.Filename).ToLowerInvariant())).ToList();
						if ((ivlLinks != null) && (ivlLinks.Count > 0))
						{
							foreach (IVirtualModLink Link in ivlLinks)
								RemoveFileLink(Link, p_modMod, p_booPurging);
						}
					}

					TxFileManager tfmFileManager = new TxFileManager();
					IIniInstaller iniIniInstaller = new IniInstaller(p_modMod, ModInstallLog, null, tfmFileManager, null);
					IList<IniEdit> lstIniEdits = ModInstallLog.GetInstalledIniEdits(p_modMod);
					foreach (IniEdit iniEdit in lstIniEdits)
						iniIniInstaller.UneditIni(iniEdit.File, iniEdit.Section, iniEdit.Key);

					RemoveIniEdits(p_modMod);

					m_tslVirtualModInfo.RemoveAll(x => x.ModFileName.ToLowerInvariant() == Path.GetFileName(p_modMod.Filename).ToLowerInvariant());

					if (!p_booPurging)
						SaveList();

					if (GameMode.RequiresModFileMerge)
					{
						List<IMod> ActiveMods;
						ActiveMods = ModManager.ActiveMods.Where(x => ActiveModList.Contains(Path.GetFileName(x.Filename), StringComparer.CurrentCultureIgnoreCase)).ToList();
						GameMode.ModFileMerge(ActiveMods, p_modMod, true);
					}

					if (!p_booPurging)
						if (this.ModActivationChanged != null)
							this.ModActivationChanged(this, new EventArgs());
				}
			}
		}

		public void FinalizeModDeactivation(IMod p_modMod)
		{
			TxFileManager tfmFileManager = new TxFileManager();
			IIniInstaller iniIniInstaller = new IniInstaller(p_modMod, ModInstallLog, null, tfmFileManager, null);
			IList<IniEdit> lstIniEdits = ModInstallLog.GetInstalledIniEdits(p_modMod);
			foreach (IniEdit iniEdit in lstIniEdits)
				iniIniInstaller.UneditIni(iniEdit.File, iniEdit.Section, iniEdit.Key);

			RemoveIniEdits(p_modMod);

			m_tslVirtualModInfo.RemoveAll(x => x.ModFileName.ToLowerInvariant() == Path.GetFileName(p_modMod.Filename).ToLowerInvariant());

			SaveList();

			if (GameMode.RequiresModFileMerge)
			{
				List<IMod> ActiveMods;
				ActiveMods = ModManager.ActiveMods.Where(x => ActiveModList.Contains(Path.GetFileName(x.Filename), StringComparer.CurrentCultureIgnoreCase)).ToList();
				GameMode.ModFileMerge(ActiveMods, p_modMod, true);
			}

			if (this.ModActivationChanged != null)
				this.ModActivationChanged(this, new EventArgs());
		}

		public void EnableMod(IMod p_modMod)
		{
			string strVirtualFolderPath = Path.Combine(m_strVirtualActivatorPath, Path.GetFileNameWithoutExtension(p_modMod.Filename));
			string strLinkFolderPath = String.Empty;

			if (MultiHDMode)
				strLinkFolderPath = Path.Combine(HDLinkFolder, Path.GetFileNameWithoutExtension(p_modMod.Filename));

			m_booDisableIniLogging = true;

			if (Directory.Exists(strVirtualFolderPath) || (MultiHDMode && Directory.Exists(strLinkFolderPath)))
			{
				List<string> lstFiles = Directory.GetFiles(strVirtualFolderPath, "*", SearchOption.AllDirectories).ToList();
				if (MultiHDMode)
					lstFiles.AddRange(Directory.GetFiles(strLinkFolderPath, "*", SearchOption.AllDirectories));


				IModLinkInstaller ModLinkInstaller = GetModLinkInstaller();

				foreach (string File in lstFiles)
				{
					string strFile = File.Replace((strVirtualFolderPath + Path.DirectorySeparatorChar), String.Empty);
					if (Path.IsPathRooted(strFile))
						strFile = File.Replace((strLinkFolderPath + Path.DirectorySeparatorChar), String.Empty);

					string strFileLink = ModLinkInstaller.AddFileLink(p_modMod, strFile, File, false);
					
					if (!string.IsNullOrEmpty(strFileLink))
						if (PluginManager != null)
							if (PluginManager.IsActivatiblePluginFile(strFileLink))
							{
								PluginManager.AddPlugin(strFileLink);
								PluginManager.ActivatePlugin(strFileLink);
							}
				}
				LoadIniEdits(p_modMod);
				SaveList();

				if (this.ModActivationChanged != null)
					this.ModActivationChanged(p_modMod, new EventArgs());
			}
			m_booDisableIniLogging = false;
		}

		public void FinalizeModActivation(IMod p_modMod)
		{
			if (GameMode.RequiresModFileMerge)
			{
				List<IMod> ActiveMods;
				ActiveMods = ModManager.ActiveMods.Where(x => !x.Filename.Equals(p_modMod.Filename, StringComparison.CurrentCultureIgnoreCase) && ActiveModList.Contains(Path.GetFileName(x.Filename), StringComparer.CurrentCultureIgnoreCase)).ToList();
				GameMode.ModFileMerge(ActiveMods, p_modMod, false);
			}

			LoadIniEdits(p_modMod);
			SaveList();

			if (this.ModActivationChanged != null)
				this.ModActivationChanged(p_modMod, new EventArgs());
		}

		public void LogIniEdits(IMod p_modMod, string p_strSettingsFileName, string p_strSection, string p_strKey, string p_strValue)
		{
			XDocument docIniEdits;

			if (!File.Exists(m_strVirtualActivatorIniEditsPath))
			{
				docIniEdits = new XDocument();
				XElement xelRoot = new XElement("virtualModActivator", new XAttribute("fileVersion", CURRENT_VERSION));
				docIniEdits.Add(xelRoot);
			}
			else
				docIniEdits = XDocument.Load(m_strVirtualActivatorIniEditsPath);

			XElement xelIniEdits = docIniEdits.Descendants("iniEdits").FirstOrDefault();
			xelIniEdits.Add(new XElement("iniEdit",
								new XAttribute("modFile", p_modMod.Filename),
								new XElement("iniFile",
									new XText(p_strSettingsFileName)),
								new XElement("iniSection",
									new XText(p_strSection)),
								new XElement("iniKey",
									new XText(p_strKey)),
								new XElement("iniValue",
									new XText(p_strValue))));

			docIniEdits.Save(m_strVirtualActivatorIniEditsPath);
		}

		public void LoadIniEdits(IMod p_modMod)
		{
			XDocument docIniEdits;

			if (File.Exists(m_strVirtualActivatorIniEditsPath))
			{
				docIniEdits = XDocument.Load(m_strVirtualActivatorIniEditsPath);

				string strVersion = docIniEdits.Element("virtualModActivator").Attribute("fileVersion").Value;
				if (!(CURRENT_VERSION.ToString() == strVersion))
					throw new Exception(String.Format("Invalid Ini Edits version: {0} Expecting {1}", strVersion, CURRENT_VERSION));

				try
				{
					XElement xelIniEdits = docIniEdits.Descendants("iniEdits").FirstOrDefault();
					if ((xelIniEdits != null) && xelIniEdits.HasElements)
					{
						List<XElement> xelEdits = xelIniEdits.Elements("iniEdit").Where(x => x.Attribute("modFile").Value == p_modMod.Filename).ToList();

						if ((xelEdits != null) && (xelEdits.Count > 0))
						{
							TxFileManager tfmFileManager = new TxFileManager();
							IIniInstaller iniIniInstaller = new IniInstaller(p_modMod, ModInstallLog, null, tfmFileManager, null);
							
							foreach (XElement xelEdit in xelEdits)
							{
								string strIniFile = xelEdit.Attribute("iniFile").Value;
								string strIniSection = xelEdit.Element("iniSection").Value;
								string strIniKey = xelEdit.Element("iniKey").Value;
								string strIniValue = xelEdit.Element("iniValue").Value;

								iniIniInstaller.EditIni(strIniFile, strIniSection, strIniKey, strIniValue);
							}
						}
					}
				}
				catch { }
			}
		}

		public void RestoreIniEdits()
		{
			XDocument docIniEdits;

			if (File.Exists(m_strVirtualActivatorIniEditsPath))
			{
				docIniEdits = XDocument.Load(m_strVirtualActivatorIniEditsPath);

				string strVersion = docIniEdits.Element("virtualModActivator").Attribute("fileVersion").Value;
				if (!(CURRENT_VERSION.ToString() == strVersion))
					throw new Exception(String.Format("Invalid Ini Edits version: {0} Expecting {1}", strVersion, CURRENT_VERSION));

				try
				{
					XElement xelIniEdits = docIniEdits.Descendants("iniEdits").FirstOrDefault();
					if ((xelIniEdits != null) && xelIniEdits.HasElements)
					{
						List<XElement> xelEdits = xelIniEdits.Elements("iniEdit").ToList();

						if ((xelEdits != null) && (xelEdits.Count > 0))
						{
							TxFileManager tfmFileManager = new TxFileManager();
							IMod modLast = null;
							IMod modMod = null;
							IIniInstaller iniIniInstaller = null;

							foreach (XElement xelEdit in xelEdits)
							{
								string strIniFile = xelEdit.Attribute("iniFile").Value;
								string strIniSection = xelEdit.Element("iniSection").Value;
								string strIniKey = xelEdit.Element("iniKey").Value;
								string strIniValue = xelEdit.Element("iniValue").Value;
								string strModFile = xelEdit.Element("modFile").Value;

								if ((modLast != null) && (strModFile.ToLowerInvariant() == modLast.Filename.ToLowerInvariant()))
									modMod = modLast;
								else
								{
									modMod = ModManager.ManagedMods.Where(x => x.Filename.ToLowerInvariant() == strModFile.ToLowerInvariant()).FirstOrDefault();
									if (modMod != null)
										iniIniInstaller = new IniInstaller(modMod, ModInstallLog, null, tfmFileManager, null);
								}
									
								if (modMod != null)
								{
									iniIniInstaller.EditIni(strIniFile, strIniSection, strIniKey, strIniValue);
								}
							}
						}
					}
				}
				catch { }
			}
		}

		public void PurgeIniEdits()
		{
			XDocument docIniEdits;

			if (File.Exists(m_strVirtualActivatorIniEditsPath))
			{
				docIniEdits = XDocument.Load(m_strVirtualActivatorIniEditsPath);

				try
				{
					XElement xelIniEdits = docIniEdits.Descendants("iniEdits").FirstOrDefault();
					if ((xelIniEdits != null) && xelIniEdits.HasElements)
					{
						List<XElement> xelEdits = xelIniEdits.Elements("iniEdit").ToList();

						if ((xelEdits != null) && (xelEdits.Count > 0))
						{		
							TxFileManager tfmFileManager = new TxFileManager();
												
							foreach (string strFilename in xelEdits.Select(x => x.Element("modFile").Value).Distinct())
							{
								IMod modMod = ModManager.ManagedMods.Where(x => x.Filename == strFilename).FirstOrDefault();
								if (modMod != null)
								{
									IIniInstaller iniIniInstaller = new IniInstaller(modMod, ModInstallLog, null, tfmFileManager, null);
									IList<IniEdit> lstIniEdits = ModInstallLog.GetInstalledIniEdits(modMod);
									foreach (IniEdit iniEdit in lstIniEdits)
										iniIniInstaller.UneditIni(iniEdit.File, iniEdit.Section, iniEdit.Key);
								}
							}
						}
					}

					FileUtil.ForceDelete(m_strVirtualActivatorIniEditsPath);
				}
				catch { }
			}
		}

		private void RemoveIniEdits(IMod p_modMod)
		{
			RemoveIniEdits(p_modMod, m_strVirtualActivatorIniEditsPath);
		}

		private void RemoveIniEdits(IMod p_modMod, string p_strPath)
		{
			XDocument docIniEdits;

			if (File.Exists(p_strPath))
			{
				docIniEdits = XDocument.Load(p_strPath);

				try
				{
					XElement xelIniEdits = docIniEdits.Descendants("iniEdits").FirstOrDefault();
					if ((xelIniEdits != null) && xelIniEdits.HasElements)
					{
						List<XElement> xelEdits = xelIniEdits.Elements("iniEdit").Where(x => x.Attribute("modFile").Value == p_modMod.Filename).ToList();

						if ((xelEdits != null) && (xelEdits.Count > 0))
							foreach (XElement xelEdit in xelEdits)
								xelEdit.Remove();
					}

					docIniEdits.Save(p_strPath);
				}
				catch { }
			}
		}

		public void ImportIniEdits(string p_strIniXML)
		{
			XDocument docVirtual = XDocument.Parse(p_strIniXML);

			if (File.Exists(m_strVirtualActivatorIniEditsPath))
				FileUtil.ForceDelete(m_strVirtualActivatorIniEditsPath);

			docVirtual.Save(m_strVirtualActivatorIniEditsPath);
		}

		#endregion

		public void SetNewFolders(string p_strVirtual, string p_strLink, bool? p_booMultiHD)
		{
			if (p_booMultiHD != null)
				NewMultiHD = (bool)p_booMultiHD;
			NewVirtualFolder = p_strVirtual;
			NewLinkFolder = p_strLink;
		}

		public void CheckLinkListIntegrity(IList<IVirtualModLink> p_ivlVirtualLinks, out Dictionary<string, string> p_dicUninstalled, out Dictionary<string, string> p_dicMissing, IList<string> p_lstForced)
		{
			p_dicUninstalled = new Dictionary<string, string>();
			p_dicMissing = new Dictionary<string, string>();

			foreach (IVirtualModLink ivlModLink in p_ivlVirtualLinks)
			{
				string strBaseFileCheck = Path.Combine(VirtualFoder, ivlModLink.RealModPath);

				if (!File.Exists(strBaseFileCheck) || (MultiHDMode && (String.IsNullOrEmpty(HDLinkFolder) || (!File.Exists(Path.Combine(HDLinkFolder, ivlModLink.RealModPath))))))
				{
					List<IMod> lstMods = ModManager.ActiveMods.Where(x => Path.GetFileName(x.Filename).ToLowerInvariant() == ivlModLink.ModInfo.ModFileName.ToLowerInvariant()).ToList();
					if (lstMods != null && lstMods.Count > 0)
					{
						if (!p_dicMissing.ContainsKey(ivlModLink.ModInfo.ModFileName))
							p_dicMissing.Add(ivlModLink.ModInfo.ModFileName, strBaseFileCheck);
					}
					else if (!p_dicUninstalled.ContainsKey(ivlModLink.ModInfo.ModFileName))
						p_dicUninstalled.Add(ivlModLink.ModInfo.ModFileName, strBaseFileCheck);
				}
			}
		}

		public IModLinkInstaller GetModLinkInstaller()
		{
			return new ModLinkInstaller(this);
		}

		public void PurgeMods(List<IMod> p_lstMods, string p_strPath)
		{
			List<IVirtualModLink> lstVirtualLinks = new List<IVirtualModLink>();
			List<IVirtualModInfo> lstVirtualMods = new List<IVirtualModInfo>();
			List<string> lstPlugins = new List<string>();
			string strProfilePath = Path.Combine(p_strPath, "modlist.xml");
			LoadListOnDemand(strProfilePath, out lstVirtualLinks, out lstVirtualMods);

			if (lstVirtualLinks.Count > 0 && lstVirtualMods.Count > 0)
			{
				foreach (IMod modMod in p_lstMods)
				{
					if (GameMode.UsesPlugins)
					{
						List<IVirtualModLink> ivlLinks = lstVirtualLinks.Where(x => x.ModInfo.ModFileName.ToLowerInvariant() == Path.GetFileName(modMod.Filename).ToLowerInvariant() && (Path.GetExtension(x.VirtualModPath).ToLowerInvariant() == ".esp" || Path.GetExtension(x.VirtualModPath).ToLowerInvariant() == ".esm")).ToList();
						if (ivlLinks != null)
						{
							foreach (IVirtualModLink Link in ivlLinks)
							{
								string strPlugin = Path.GetFileName(Link.VirtualModPath).ToLowerInvariant();
								List<IVirtualModLink> ivlPlugins = lstVirtualLinks.Where(x => Path.GetFileName(x.VirtualModPath).ToLowerInvariant() == strPlugin && x.ModInfo.ModFileName.ToLowerInvariant() != Path.GetFileName(modMod.Filename).ToLowerInvariant()).ToList();
								if ((ivlPlugins == null) || (ivlPlugins.Count == 0))
									lstPlugins.Add(strPlugin);
							}
						}

						//remove from ini
						string strPath = Path.Combine(p_strPath, ACTIVATOR_INIEDITS);
						RemoveIniEdits(modMod, strPath);
					}

					//remove from modlist
					lstVirtualLinks.RemoveAll(x => x.ModInfo.ModFileName.ToLowerInvariant() == Path.GetFileName(modMod.Filename).ToLowerInvariant());
					lstVirtualMods.RemoveAll(x => x.ModFileName.ToLowerInvariant() == Path.GetFileName(modMod.Filename).ToLowerInvariant());
				}

				if (GameMode.UsesPlugins && lstPlugins.Count > 0)
				{
					//remove loadorder
					string strLoadorderPath = Path.Combine(p_strPath, "loadorder.txt");
					File.WriteAllLines(strLoadorderPath, File.ReadLines(strLoadorderPath).Where(l => !lstPlugins.Contains(l.Substring(0, l.IndexOf('=')).ToLowerInvariant())).ToList());
				}

				//save list
				string strModListPath = Path.Combine(p_strPath, "modlist.xml");
				SaveModList(strModListPath, lstVirtualMods, lstVirtualLinks);
			}
		}

		private bool CheckIsModActive(IMod p_modMod)
		{
			IVirtualModInfo ivlModInfo = null;

			if ((m_tslVirtualModInfo != null && m_tslVirtualModInfo.Count > 0)  && p_modMod != null)
				ivlModInfo = m_tslVirtualModInfo.Where(x => (x != null) && x.ModFileName.ToLowerInvariant() == Path.GetFileName(p_modMod.Filename.ToLowerInvariant())).FirstOrDefault();

			return (ivlModInfo != null);
		}

		public bool CheckHasActiveLinks(IMod p_modMod)
		{
			IVirtualModLink ivlModLink = m_tslVirtualModList.Where(x => x.ModInfo.ModFileName.ToLowerInvariant() == Path.GetFileName(p_modMod.Filename.ToLowerInvariant())).FirstOrDefault();
			return (ivlModLink != null);
		}	

		/// <summary>
		/// Deletes any empty directories found between the start path and the end directory.
		/// </summary>
		/// <param name="p_strStartPath">The path from which to start looking for empty directories.</param>
		/// <param name="p_strStopDirectory">The directory at which to stop looking.</param>
		protected void TrimEmptyDirectories(string p_strStartPath, string p_strStopDirectory)
		{
			string strEmptyDirectory = p_strStartPath;
			while (true)
			{
				if (Directory.Exists(strEmptyDirectory) &&
					(Directory.GetFiles(strEmptyDirectory).Length + Directory.GetDirectories(strEmptyDirectory).Length == 0) &&
					!strEmptyDirectory.Equals(p_strStopDirectory, StringComparison.OrdinalIgnoreCase))
				{
					for (Int32 i = 0; i < 5 && Directory.Exists(strEmptyDirectory); i++)
						FileUtil.ForceDelete(strEmptyDirectory);
				}
				else
					break;
				strEmptyDirectory = Path.GetDirectoryName(strEmptyDirectory);
			}
		}

		public string GetCurrentFileOwner(string p_strPath)
		{
			string strOwner = String.Empty;

			if ((VirtualLinks != null) && (VirtualLinks.Count > 0))
			{
				string strFile = Path.GetFileName(p_strPath);
				IVirtualModLink vmlLink = VirtualLinks.Find(x => Path.GetFileName(x.VirtualModPath).Equals(strFile, StringComparison.CurrentCultureIgnoreCase) && (x.Active == true));
				if (vmlLink != null)
					strOwner = vmlLink.ModInfo.ModName;
			}

			return strOwner;
		}

		public bool OverwriteLooseFile(string p_strFilePath, string p_strModFileName)
		{
			try
			{
				string strSource = Path.Combine(m_strGameDataPath, p_strFilePath);
				string strDest = Path.Combine(m_strVirtualActivatorOverwritePath, Path.GetFileNameWithoutExtension(p_strModFileName), p_strFilePath);

				if (File.Exists(strSource))
				{
					string strDestFolder = Path.GetDirectoryName(strDest);
					if (!Directory.Exists(strDestFolder))
					{
						Directory.CreateDirectory(strDestFolder);
					}

					if (Directory.Exists(strDestFolder))
						File.Move(strSource, strDest);
				}

				return true;
			}
			catch 
			{ 
				return false;
			}
		}

		/// <summary>
		/// Runs the managed updaters.
		/// </summary>
		/// <param name="p_rolModList">The mod list.</param>
		/// <param name="p_camConfirm">The delegate to call to confirm an action.</param>
		/// <returns>The background task that will run the updaters.</returns>
		public IBackgroundTask ActivatingMod(IMod p_modMod, bool p_booDisabling, ConfirmActionMethod p_camConfirm)
		{
			if (p_booDisabling)
				if (!CheckIsModActive(p_modMod))
					return null;
				
			LinkActivationTask latActivatingMod = new LinkActivationTask(PluginManager, this, p_modMod, p_booDisabling, p_camConfirm);
			if (GameMode.LoadOrderManager != null)
				GameMode.LoadOrderManager.MonitorExternalTask(latActivatingMod);
			else
				latActivatingMod.Update(p_camConfirm);
			return latActivatingMod;
		}

		#endregion

		#region VirtualModInfo EqualityComparer

		private class VirtualModInfoEqualityComparer : IEqualityComparer<IVirtualModInfo>
		{
			public bool Equals(IVirtualModInfo x, IVirtualModInfo y)
			{
				if (object.ReferenceEquals(x, y))
					return true;
				if (x == null || y == null)
					return false;
				return (x.ModFileName.Equals(y.ModFileName, StringComparison.InvariantCultureIgnoreCase));
			}

			public int GetHashCode(IVirtualModInfo obj)
			{
				return obj.ModFileName.GetHashCode();
 			}
 		}

		#endregion

		#region Registry

		/// <summary>
		/// Check the correct path on the Registry.
		/// </summary>
		/// <param name="strGameMode">The selected game mode.</param>
		/// <param name="strMods">The selected Mods path.</param>
		/// <param name="strInstallInfo">The selected Install Info path.</param>
		public void SaveRegistry(string p_strGameMode, string p_strVirtual, string p_strHDLink, bool p_booMultiHDInstall)
		{
			try
			{
				RegistryKey rkKey = null;
				string strNMMKey = @"SOFTWARE\NexusModManager\";
				string strGameKey = @"SOFTWARE\NexusModManager\" + p_strGameMode;

				if (RegistryUtil.CanReadKey(strNMMKey) && RegistryUtil.CanWriteKey(strNMMKey))
				{
					rkKey = Registry.LocalMachine.OpenSubKey(strNMMKey, true);
					if (rkKey == null)
						if (RegistryUtil.CanCreateKey(strNMMKey))
							Registry.LocalMachine.CreateSubKey(strNMMKey);
				}

				if (rkKey != null)
				{
					if (RegistryUtil.CanCreateKey(strGameKey))
						Registry.LocalMachine.CreateSubKey(strGameKey);

					if (RegistryUtil.CanReadKey(strGameKey) && RegistryUtil.CanWriteKey(strGameKey))
					{
						rkKey = Registry.LocalMachine.OpenSubKey(strGameKey, true);
						rkKey.SetValue("Virtual", p_strVirtual);
						rkKey.SetValue("HDLink", p_strHDLink);
						rkKey.SetValue("MultiHDInstall", p_booMultiHDInstall);
					}
				}
			}
			catch
			{
				return;
			}
		}

		#endregion
	}
}

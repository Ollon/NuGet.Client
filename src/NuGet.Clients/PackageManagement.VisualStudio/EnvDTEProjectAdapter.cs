// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using NuGet.Frameworks;
using VSLangProj;
using VSLangProj150;

namespace NuGet.PackageManagement.VisualStudio
{
    /// <summary>
    /// An adapter for DTE calls, to allow simplified calls and mocking for tests.
    /// </summary>
    public class EnvDTEProjectAdapter : IEnvDTEProjectAdapter
    {
        /// <summary>
        /// The adaptee for this adapter
        /// </summary>
        private readonly Project _project;

        // Interface casts
        private IVsBuildPropertyStorage _asIVsBuildPropertyStorage;
        private IVsHierarchy _asIVsHierarchy;
        private VSProject _asVSProject;
        private VSProject4 _asVSProject4;

        // Property caches
        private string _projectFullPath;
        private string _baseIntermediatePath;
        private bool? _isLegacyCSProjPackageReferenceProject;

        public EnvDTEProjectAdapter(Project project)
        {
            if (project == null)
            {
                throw new ArgumentNullException(nameof(project));
            }

            _project = project;
        }

        public Project DTEProject
        {
            get
            {
                // Note that we don't throw when not on UI thread for extracting this object. It is completely uncurated
                // access to the DTE project object, and any code using it must be responsible in its thread management.
                return _project;
            }
        }

        public string Name
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                // Uncached, in case project is renamed
                return _project.Name;
            }
        }

        public string UniqueName
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                // Uncached, in case project is renamed
                return _project.UniqueName;
            }
        }

        public string ProjectFullPath
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                return string.IsNullOrEmpty(_projectFullPath) ?
                    (_projectFullPath = EnvDTEProjectUtility.GetFullProjectPath(_project)) :
                    _projectFullPath;
            }
        }

        public string BaseIntermediatePath
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                if (string.IsNullOrEmpty(_baseIntermediatePath))
                {
                    if (AsIVsBuildPropertyStorage != null)
                    {
                        var intermediateDirectory = GetMSBuildProperty(AsIVsBuildPropertyStorage, "BaseIntermediateOutputPath");
                        var projectDirectory = Path.GetDirectoryName(ProjectFullPath);
                        if (string.IsNullOrEmpty(intermediateDirectory))
                        {
                            _baseIntermediatePath = Path.GetDirectoryName(projectDirectory);
                        }
                        else
                        {
                            if (!Path.IsPathRooted(intermediateDirectory))
                            {
                                _baseIntermediatePath = Path.Combine(projectDirectory, intermediateDirectory);
                            }
                        }
                    }
                }

                return _baseIntermediatePath;
            }
        }

        public bool IsLegacyCSProjPackageReferenceProject
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                if (!_isLegacyCSProjPackageReferenceProject.HasValue)
                {
                    // A legacy CSProj can't be CPS, must cast to VSProject4 and *must* have at least one package
                    // reference already in the CSProj. In the future this logic may change. For now a user must
                    // hand code their first package reference. Laid out in longhand for readability.
                    if (AsIVsHierarchy?.IsCapabilityMatch("CPS") ?? true)
                    {
                        _isLegacyCSProjPackageReferenceProject = false;
                    }
                    else if (AsVSProject4 == null)
                    {
                        _isLegacyCSProjPackageReferenceProject = false;
                    }
                    else if ((AsVSProject4.PackageReferences?.InstalledPackages?.Length ?? 0) == 0)
                    {
                        _isLegacyCSProjPackageReferenceProject = false;
                    }
                    else
                    {
                        _isLegacyCSProjPackageReferenceProject = true;
                    }
                }

                return _isLegacyCSProjPackageReferenceProject.Value;
            }
        }

        public NuGetFramework TargetNuGetFramework
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                // Uncached, in case project file edited
                return EnvDTEProjectUtility.GetTargetNuGetFramework(_project);
            }
        }

        private IVsHierarchy AsIVsHierarchy
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                return _asIVsHierarchy ?? (_asIVsHierarchy = VsHierarchyUtility.ToVsHierarchy(_project));
            }
        }

        private IVsBuildPropertyStorage AsIVsBuildPropertyStorage
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                return _asIVsBuildPropertyStorage ?? (_asIVsBuildPropertyStorage = AsIVsHierarchy as IVsBuildPropertyStorage);
            }
        }

        private VSProject AsVSProject
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                return _asVSProject ?? (_asVSProject = _project.Object as VSProject);
            }
        }

        private VSProject4 AsVSProject4
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                return _asVSProject4 ?? (_asVSProject4 = _project.Object as VSProject4);
            }
        }

        public IEnumerable<LegacyCSProjProjectReference> GetLegacyCSProjProjectReferences(Array desiredMetadata)
        {
            if (!IsLegacyCSProjPackageReferenceProject)
            {
                throw new InvalidOperationException("Project reference call made on a non-legacy CSProj project");
            }

            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (Reference reference in AsVSProject4.References)
            {
                var reference6 = reference as Reference6;
                if (reference6 != null && reference.SourceProject != null)  // If there is a SourceProject, this must be a P2P reference
                {
                    Array metadataElements;
                    Array metadataValues;
                    reference6.GetMetadata(desiredMetadata, out metadataElements, out metadataValues);

                    yield return new LegacyCSProjProjectReference(
                        uniqueName: reference.SourceProject.FullName, 
                        metadataElements: metadataElements,
                        metadataValues: metadataValues);
                }
            }
        }

        public IEnumerable<LegacyCSProjPackageReference> GetLegacyCSProjPackageReferences(Array desiredMetadata)
        {
            if (!IsLegacyCSProjPackageReferenceProject)
            {
                throw new InvalidOperationException("Package reference call made on a non-legacy CSProj project");
            }

            ThreadHelper.ThrowIfNotOnUIThread();

            var installedPackages = AsVSProject4.PackageReferences.InstalledPackages;
            var packageReferences = new List<LegacyCSProjPackageReference>();

            foreach (var installedPackage in installedPackages)
            {
                var installedPackageName = installedPackage as string;
                if (!string.IsNullOrEmpty(installedPackageName))
                {
                    string version;
                    Array metadataElements;
                    Array metadataValues;
                    AsVSProject4.PackageReferences.TryGetReference(installedPackageName, desiredMetadata, out version, out metadataElements, out metadataValues);

                    yield return new LegacyCSProjPackageReference(
                        name: installedPackageName,
                        version: version,
                        metadataElements: metadataElements,
                        metadataValues: metadataValues,
                        targetNuGetFramework: TargetNuGetFramework);
                }
            }
        }

        public void AddOrUpdateLegacyCSProjPackage(string packageName, string packageVersion, string[] metadataElements, string[] metadataValues)
        {
            if (!IsLegacyCSProjPackageReferenceProject || AsVSProject4 == null)
            {
                throw new InvalidOperationException("Cannot add packages to a project which is not a legacy CSProj package reference project");
            }

            ThreadHelper.ThrowIfNotOnUIThread();

            AsVSProject4.PackageReferences.AddOrUpdate(packageName, packageVersion, metadataElements, metadataValues);
        }

        public void RemoveLegacyCSProjPackage(string packageName)
        {
            if (!IsLegacyCSProjPackageReferenceProject || AsVSProject4 == null)
            {
                throw new InvalidOperationException("Cannot remove packages from a project which is not a legacy CSProj package reference project");
            }

            ThreadHelper.ThrowIfNotOnUIThread();

            AsVSProject4.PackageReferences.Remove(packageName);
        }

        private static string GetMSBuildProperty(IVsBuildPropertyStorage buildPropertyStorage, string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string output;
            var result = buildPropertyStorage.GetPropertyValue(
                name,
                string.Empty,
                (uint)_PersistStorageType.PST_PROJECT_FILE,
                out output);

            if (result != NuGetVSConstants.S_OK || string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            return output;
        }
    }
}

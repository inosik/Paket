﻿module internal Paket.NupkgWriter

open System
open System.IO
open System.Xml.Linq
open System.IO.Compression
open Paket
open System.Text
open System.Text.RegularExpressions
open System.Xml

let nuspecId = "nuspec"
let corePropsId = "coreProp"
let contentTypePath = "[Content_Types].xml"

let contentTypeDoc fileList =
    let declaration = XDeclaration("1.0", "UTF-8", "yes")
    let ns = XNamespace.Get "http://schemas.openxmlformats.org/package/2006/content-types"
    let root = XElement(ns + "Types")

    let defaultNode extension contentType =
        let def = XElement(ns + "Default")
        def.SetAttributeValue(XName.Get "Extension", extension)
        def.SetAttributeValue(XName.Get "ContentType", contentType)
        def

    let knownExtensions =
        Map.ofList [ "rels", "application/vnd.openxmlformats-package.relationships+xml"
                     "psmdcp", "application/vnd.openxmlformats-package.core-properties+xml" ]

    let ext path = Path.GetExtension(path).TrimStart([| '.' |]).ToLowerInvariant()

    let fType ext =
        knownExtensions
        |> Map.tryFind ext
        |> function
        | Some ft -> ft
        | None -> "application/octet"

    let contentTypes =
        fileList
        |> Seq.choose (fun f ->
               let e = ext f
               if String.IsNullOrWhiteSpace e then
                 None
               else Some(e, fType e))
        |> Seq.distinct
        |> Seq.iter (fun (ex, ct) -> defaultNode ex ct |> root.Add)

    XDocument(declaration, box root)

let nuspecDoc (info:CompleteInfo) =
    let core,optional = info
    let declaration = XDeclaration("1.0", "UTF-8", "yes")
    let ns = XNamespace.Get "http://schemas.microsoft.com/packaging/2011/10/nuspec.xsd"
    let root = XElement(ns + "package")

    let addChildNode (parent : XElement) name value =
        let node = XElement(ns + name)
        node.SetValue value
        parent.Add node

    let metadataNode = XElement(ns + "metadata")
    root.Add metadataNode
    let (!!) = addChildNode metadataNode

    let (!!?) nodeName strOpt =
        match strOpt with
        | Some s -> addChildNode metadataNode nodeName s
        | None -> ()

    let buildFrameworkReferencesNode libName =
        let element = XElement(ns + "frameworkAssembly")
        element.SetAttributeValue(XName.Get "assemblyName", libName)
        element

    let buildFrameworkReferencesNode frameworkAssembliesList =
        if List.isEmpty frameworkAssembliesList then () else
        let d = XElement(ns + "frameworkAssemblies")
        frameworkAssembliesList |> List.iter (buildFrameworkReferencesNode >> d.Add)
        metadataNode.Add d

    let buildDependencyNode (Id, requirement:VersionRequirement) =
        let dep = XElement(ns + "dependency")
        dep.SetAttributeValue(XName.Get "id", Id)
        dep.SetAttributeValue(XName.Get "version", requirement.FormatInNuGetSyntax())
        dep

    let buildDependenciesNode excludedDependencies dependencyList =
        if  List.isEmpty dependencyList then () else
        let d = XElement(ns + "dependencies")
        dependencyList 
        |> List.filter (fun d -> Set.contains (fst d) excludedDependencies |> not)
        |> List.iter (buildDependencyNode >> d.Add)
        metadataNode.Add d

    let buildReferenceNode (fileName) =
        let dep = XElement(ns + "reference")
        dep.SetAttributeValue(XName.Get "file", fileName)
        dep

    let buildReferencesNode referenceList =
        if List.isEmpty referenceList then () else
        let d = XElement(ns + "references")
        referenceList |> List.iter (buildReferenceNode >> d.Add)
        metadataNode.Add d

    !! "id" core.Id
    match core.Version with
    | Some v -> !! "version" <| v.ToString()
    | None -> failwithf "No version was given for %s" core.PackageFileName
    (!!?) "title" optional.Title
    !! "authors" (core.Authors |> String.concat ", ")
    if optional.Owners <> [] then !! "owners" (String.Join(", ",optional.Owners))
    (!!?) "licenseUrl" optional.LicenseUrl
    (!!?) "projectUrl" optional.ProjectUrl
    (!!?) "iconUrl" optional.IconUrl
    if optional.RequireLicenseAcceptance then
        !! "requireLicenseAcceptance" "true"
    !! "description" core.Description
    (!!?) "summary" optional.Summary
    (!!?) "releaseNotes" optional.ReleaseNotes
    (!!?) "copyright" optional.Copyright
    (!!?) "language" optional.Language
    if optional.Tags <> [] then !! "tags" (String.Join(" ",optional.Tags))
    if optional.DevelopmentDependency  then
        !! "developmentDependency" "true"

    optional.References |> buildReferencesNode
    optional.FrameworkAssemblyReferences |> buildFrameworkReferencesNode
    optional.Dependencies |> buildDependenciesNode optional.ExcludedDependencies
    XDocument(declaration, box root)

let corePropsPath = sprintf "package/services/metadata/core-properties/%s.psmdcp" corePropsId

let corePropsDoc (core : CompleteCoreInfo) =
    let declaration = XDeclaration("1.0", "UTF-8", "yes")
    let ns = XNamespace.Get "http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
    let dc = XNamespace.Get "http://purl.org/dc/elements/1.1/"
    let dcterms = XNamespace.Get "http://purl.org/dc/terms/"
    let xsi = XNamespace.Get "http://www.w3.org/2001/XMLSchema-instance"
    let root =
        XElement
            (ns + "coreProperties", XAttribute(XName.Get "xmlns", ns.NamespaceName),
             XAttribute(XNamespace.Xmlns + "dc", dc.NamespaceName),
             XAttribute(XNamespace.Xmlns + "dcterms", dcterms.NamespaceName),
             XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName))

    let (!!) (ns : XNamespace) name value =
        let node = XElement(ns + name)
        node.SetValue value
        root.Add node
    !! dc "creator" core.Authors
    !! dc "description" core.Description
    !! dc "identifier" core.Id
    !! ns "version" core.Version
    XElement(ns + "keywords") |> root.Add
    !! dc "title" core.Id
    !! ns "lastModifiedBy" "paket"
    XDocument(declaration, box root)

let relsPath = "_rels/.rels"

let relsDoc (core : CompleteCoreInfo) =
    let declaration = XDeclaration("1.0", "UTF-8", "yes")
    let ns = XNamespace.Get "http://schemas.openxmlformats.org/package/2006/relationships"
    let root = XElement(ns + "Relationships")

    let r type' target id' =
        let rel = XElement(ns + "Relationship")
        rel.SetAttributeValue(XName.Get "Type", type')
        rel.SetAttributeValue(XName.Get "Target", target)
        rel.SetAttributeValue(XName.Get "Id", id')
        root.Add rel
    r "http://schemas.microsoft.com/packaging/2010/07/manifest" ("/" + core.NuspecFileName) nuspecId
    r "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" ("/" + corePropsPath) corePropsId
    XDocument(declaration, box root)

let xDocWriter (xDoc : XDocument) (stream : System.IO.Stream) =
    let settings = new XmlWriterSettings(Indent = true, Encoding = Encoding.UTF8)
    use xmlWriter = XmlWriter.Create(stream, settings)
    xDoc.WriteTo xmlWriter
    xmlWriter.Flush()

let writeNupkg  (core : CompleteCoreInfo) optional =
    [ core.NuspecFileName, nuspecDoc(core,optional) |> xDocWriter
      corePropsPath, corePropsDoc core |> xDocWriter
      relsPath, relsDoc core |> xDocWriter ]

let Write (core : CompleteCoreInfo) optional workingDir outputDir =
    let outputPath = Path.Combine(outputDir, core.PackageFileName)
    if File.Exists outputPath then
        File.Delete outputPath

    use zipToCreate = new FileStream(outputPath, FileMode.Create)
    use zipFile = ZipStorer.Create (zipToCreate, null)

    let entries = System.Collections.Generic.List<_>()

    let fixRelativePath (p:string) =
        let isWinDrive = Regex(@"^\w:\\.*", RegexOptions.Compiled).IsMatch
        let isNixRoot = Regex(@"^\/.*", RegexOptions.Compiled).IsMatch

        let prepend,path =
            match p with
            | s when isWinDrive s -> [|s.Substring(0,3)|],s.Substring(3)
            | s when isNixRoot s -> [|"/"|],s.Substring(1)
            | s when String.IsNullOrWhiteSpace s -> failwith "Empty exclusion path!"
            | s -> [||],s

        path.Split('\\','/')
        |> Array.fold (fun (xs:string []) x ->
            match x with
            | s when "..".Equals s -> Array.sub xs 0 (xs.Length-1)
            | s when ".".Equals s -> xs
            | _ -> Array.append xs [|x|]) [||]
        |> Array.append prepend
        |> Array.fold (fun p' x -> Path.Combine(p',x)) ""

    let exclusions =
        optional.FilesExcluded
        |> List.map (fun e -> Path.Combine(workingDir,e) |> fixRelativePath |> Fake.Globbing.isMatch)

    let isExcluded p =
        let path = DirectoryInfo(p).FullName
        exclusions |> List.exists (fun f -> f path)

    let ensureValidName (target: string) =
        // Some characters that are considered reserved by RFC 2396
        // and thus escaped by Uri.EscapeDataString, are valid in folder names.
        // Concrete problem solved here:
        // Creating deployable packages for javascript applications
        // that use javascript packages from NPM, where the @ char
        // is used in folder names to separate versions.
        //
        // Ref: https://msdn.microsoft.com/en-us/library/system.uri.escapedatastring(v=vs.110).aspx#Anchor_2
        //      http://tools.ietf.org/html/rfc2396#section-2
        let problemChars = ["@","~~at~~"]

        let fakeEscapeProblemChars (source:string) =
            problemChars 
            |> List.fold (fun (escaped:string) (problem, fakeEscape) -> 
                escaped.Replace(problem,fakeEscape)) source 

        let unFakeEscapeProblemChars (source:string) = 
            problemChars 
            |> List.fold (fun (escaped:string) (problem, fakeEscape) -> 
                escaped.Replace(fakeEscape, problem)) source 

        let escapeTarget (target:string) = 
            let escapedTargetParts = 
                target.Replace("\\", "/").Split('/') 
                |> Array.map Uri.EscapeDataString
            String.Join("/" ,escapedTargetParts)

        let toUri (escapedTarget:string) = 
            let uri1 = Uri(escapedTarget, UriKind.Relative)
            let uri2 = Uri(uri1.GetComponents(UriComponents.SerializationInfoString, UriFormat.SafeUnescaped), UriKind.Relative)
            uri2.GetComponents(UriComponents.SerializationInfoString, UriFormat.UriEscaped)

        target
        |> fakeEscapeProblemChars 
        |> escapeTarget
        |> unFakeEscapeProblemChars
        |> toUri 

    let addEntry path writerF =
        if entries.Contains(path) then () else
        entries.Add path |> ignore
        use stream = new MemoryStream()
        writerF stream
        zipFile.AddStream (ZipStorer.Compression.Deflate, path, stream, DateTime.UtcNow, null)
        stream.Close()

    let addEntryFromFile path source =
        if entries.Contains(path) then () else
        entries.Add path |> ignore
        zipFile.AddFile (ZipStorer.Compression.Deflate, source, path, null)

    let ensureValidTargetName (target:string) =
        let target = ensureValidName target

        match target with
        | t when t.EndsWith("/")         -> t
        | t when String.IsNullOrEmpty(t) -> ""
        | "."                            -> ""
        | t                              -> t + "/"

    // adds all files in a directory to the zipFile
    let rec addDir source target =
        if not <| isExcluded source then
            let target = ensureValidTargetName target
            for file in Directory.EnumerateFiles(source,"*.*",SearchOption.TopDirectoryOnly) do
                if not <| isExcluded file then
                    let fi = FileInfo file
                    let fileName = ensureValidName fi.Name
                    let path = Path.Combine(target,fileName)

                    addEntryFromFile path fi.FullName

            for dir in Directory.EnumerateDirectories(source,"*",SearchOption.TopDirectoryOnly) do
                let di = DirectoryInfo dir
                addDir di.FullName (Path.Combine(target,di.Name))

    // add files
    for fileName,targetFileName in optional.Files do
        let targetFileName = ensureValidTargetName targetFileName
        let source = Path.Combine(workingDir, fileName)
        if Directory.Exists source then
            addDir source targetFileName
        else
            if File.Exists source then
                if not <| isExcluded source then
                    let fi = FileInfo(source)
                    let fileName = ensureValidName fi.Name
                    let path = Path.Combine(targetFileName,fileName)
                    addEntryFromFile path source
            else
                failwithf "Could not find source file %s" source

    // add metadata
    for path, writer in writeNupkg core optional do
        addEntry path writer

    entries
    |> Seq.toList
    |> contentTypeDoc
    |> xDocWriter
    |> addEntry contentTypePath

    outputPath

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.VFX;
using System;
using System.Linq;

// Primarily for VFX graph.
[ExecuteAlways]
public class MultiMeshToSDF : MonoBehaviour
{
    [Serializable]
    public class SkinnedOutputs
    {
        public SkinnedMeshRenderer skinnedMeshRenderer;
        [HideInInspector]
        public Mesh tempMesh;
        [HideInInspector]
        public CombineInstance combine;
    }

    public bool executeInEditMode = false;

    public ComputeShader JFAImplementation;
    public ComputeShader MtVImplementation;

    [HideInInspector]
    public RenderTexture outputRenderTexture;

    [Tooltip("Visual effect whose property to set with the output SDF texture")]
    public VisualEffect vfxOutput;
    [Tooltip("VFX Property for the SDF map")]
    public string vfxProperty;
    [Tooltip("VFX Property for the position and scale of the bounding box")]
    public string vfxTransformProperty;
    [Tooltip("The list of skinned mesh renderers used to drive the signed distance field")]
    public List<SkinnedOutputs> skinnedMeshes = new List<SkinnedOutputs>();
    Bounds outputCombinedBounds = new Bounds();
    Mesh outputMesh, emptyMesh;

    public MeshFilter filterOutput;

    [Tooltip("Material whose property to set with the output SDF texture")]
    public Material materialOutput;
    public string materialProperty = "_Texture3D";

    [Tooltip("Signed distance field resoluton")]
    public int sdfResolution = 64;

    Vector3 offset = new Vector3(0.5f,0.5f,0.5f);
    // 0.8f offset by default allows for the SDF to cleanly encapsulate the meshes at all times
    [Tooltip("Scale the mesh before voxelization")]
    public float scaleBy = 0.8f;

    public Vector3 currentScale = Vector3.zero, currentCenter = Vector3.zero;
    [Tooltip("Number of points to sample on each triangle when voxeling")]
    public uint samplesPerTriangle = 10;
    [Tooltip("Thicken the signed distance field by this amount")]
    public float postProcessThickness = 0.01f;

    // kernel ids
    int JFA;
    int Preprocess;
    int Postprocess;
    int DebugSphere;
    int MtV;
    int Zero;

#if UNITY_EDITOR
    private void OnValidate() {
        Awake();
    }
#endif

    private void Awake() {
        JFA = JFAImplementation.FindKernel("JFA");
        Preprocess = JFAImplementation.FindKernel("Preprocess");
        Postprocess = JFAImplementation.FindKernel("Postprocess");
        DebugSphere = JFAImplementation.FindKernel("DebugSphere");

        MtV = MtVImplementation.FindKernel("MeshToVoxel");
        Zero = MtVImplementation.FindKernel("Zero");
        // set to nearest power of 2
        sdfResolution = Mathf.CeilToInt(Mathf.Pow(2, Mathf.Ceil(Mathf.Log(sdfResolution, 2))));
        if (outputRenderTexture != null) outputRenderTexture.Release();
        outputRenderTexture = null;

        emptyMesh = new Mesh();
        emptyMesh.vertices = new Vector3[] { Vector3.zero, Vector3.zero, Vector3.zero };
        emptyMesh.triangles = new int[] { 0, 1, 2 };
        emptyMesh.RecalculateBounds();
    }

    private void Update() {
        if (!Application.IsPlaying(gameObject) && !executeInEditMode) return;

        if (outputMesh == null)
            outputMesh = new Mesh();

        float t = Time.time;

        bool activeItems = false;
        for (int i = 0; i < skinnedMeshes.Count; i++)
        {
            if (!skinnedMeshes[i].skinnedMeshRenderer.gameObject.activeInHierarchy)
                continue;

            activeItems = true;
            break;
        }

        // Send a tiny empty mesh if nothing in the SDF.
        if (!activeItems)
        {
            outputMesh = emptyMesh;
            currentScale = Vector3.zero;
            currentCenter = Vector3.zero;
            outputCombinedBounds = emptyMesh.bounds;
        }
        else
        {
            currentScale = new Vector3(1f / outputCombinedBounds.size.x, 1f / outputCombinedBounds.size.y, 1f / outputCombinedBounds.size.z) * scaleBy;
            currentCenter = outputCombinedBounds.center;
            for (int i = 0; i < skinnedMeshes.Count; i++)
            {
                if (!skinnedMeshes[i].skinnedMeshRenderer.gameObject.activeInHierarchy)
                    continue;

                if (skinnedMeshes[i].combine.mesh == null)
                    skinnedMeshes[i].combine.mesh = new Mesh();
                if (skinnedMeshes[i].tempMesh == null)
                    skinnedMeshes[i].tempMesh = new Mesh();

                skinnedMeshes[i].combine.transform = skinnedMeshes[i].skinnedMeshRenderer.transform.localToWorldMatrix;
                skinnedMeshes[i].skinnedMeshRenderer.BakeMesh(skinnedMeshes[i].combine.mesh);

            }

            outputMesh.CombineMeshes(skinnedMeshes.Where(r => r.skinnedMeshRenderer.gameObject.activeInHierarchy).Select(s => s.combine).ToArray());
            if (filterOutput)
                filterOutput.mesh = outputMesh;

            outputMesh.RecalculateBounds();
            outputCombinedBounds = outputMesh.bounds;
        }
        
        outputRenderTexture = MeshToVoxel(sdfResolution, outputMesh, outputCombinedBounds.center,
                offset, currentScale, samplesPerTriangle,
                outputRenderTexture);

        FloodFillToSDF(outputRenderTexture);

        if (vfxOutput)
        {
            if (!vfxOutput.HasTexture(vfxProperty))
            {
                Debug.LogError(string.Format("Vfx Output doesn't have property {0}", vfxProperty));
            }
            vfxOutput.SetTexture(vfxProperty, outputRenderTexture);
            vfxOutput.SetVector3(vfxTransformProperty + "_position", (outputCombinedBounds.center));
            vfxOutput.SetVector3(vfxTransformProperty + "_scale", new Vector3(1f / currentScale.x, 1f / currentScale.y, 1f / currentScale.z));
        }

        if (materialOutput)
        {
            if (!materialOutput.HasProperty(materialProperty))
            {
                Debug.LogError(string.Format("Material output doesn't have property {0}", materialProperty));
            }
            else
            {
                materialOutput.SetTexture(materialProperty, outputRenderTexture);
            }
        }
    }

    private void OnDrawGizmos()
    {
        if (skinnedMeshes.Count == 0)
            return;

        Gizmos.DrawWireCube(outputCombinedBounds.center, outputCombinedBounds.size);
    }

    private void OnDestroy() {
        if(outputRenderTexture != null) outputRenderTexture.Release();
        cachedBuffers[0]?.Dispose();
        cachedBuffers[1]?.Dispose();
    }

    public void FloodFillToSDF(RenderTexture voxels) {
        int dispatchCubeSize = voxels.width;
        JFAImplementation.SetInt("dispatchCubeSide", dispatchCubeSize);

        JFAImplementation.SetTexture(Preprocess, "Voxels", voxels);
        JFAImplementation.Dispatch(Preprocess, numGroups(voxels.width, 8),
                numGroups(voxels.height, 8), numGroups(voxels.volumeDepth, 8));

        JFAImplementation.SetTexture(JFA, "Voxels", voxels);
        
        /*JFAImplementation.Dispatch(JFA, numGroups(voxels.width, 8),
            numGroups(voxels.height, 8), numGroups(voxels.volumeDepth, 8)); */
        
        for (int offset = voxels.width / 2; offset >= 1; offset /= 2) {
            JFAImplementation.SetInt("samplingOffset", offset);
            JFAImplementation.Dispatch(JFA, numGroups(voxels.width, 8),
                numGroups(voxels.height, 8), numGroups(voxels.volumeDepth, 8));
        }

        JFAImplementation.SetFloat("postProcessThickness", postProcessThickness);
        JFAImplementation.SetTexture(Postprocess, "Voxels", voxels);

        JFAImplementation.Dispatch(Postprocess, numGroups(voxels.width, 8),
            numGroups(voxels.height, 8), numGroups(voxels.volumeDepth, 8));
    }

    ComputeBuffer[] cachedBuffers = new ComputeBuffer[2];

    ComputeBuffer cachedComputeBuffer(int count, int stride, int cacheSlot) {
        cacheSlot = cacheSlot == 0 ? 0 : 1;
        var buffer = cachedBuffers[cacheSlot];
        if(buffer == null || (buffer.stride != stride || buffer.count != count)) {
            if(buffer != null) buffer.Dispose();
            buffer = new ComputeBuffer(count, stride);
            cachedBuffers[cacheSlot] = buffer;
            return buffer;
        } else {
            return buffer;
        }
    }

    private Vector3 Div(Vector3 a, Vector3 b) {
        return new Vector3(a.x / b.x, a.y / b.y, a.z / b.z);
    }

    public RenderTexture MeshToVoxel(int voxelResolution, Mesh mesh, Vector3 center,
        Vector3 offset, Vector3 scaleMeshBy, uint numSamplesPerTriangle,
        RenderTexture voxels = null) {
        var indicies = mesh.triangles;
        var numIdxes = indicies.Length;
        var numTris = numIdxes / 3;
        var indicesBuffer = cachedComputeBuffer(numIdxes, sizeof(uint), 0);
        indicesBuffer.SetData(indicies);

        var vertexBuffer = cachedComputeBuffer(mesh.vertexCount, sizeof(float) * 3, 1);
        var verts = mesh.vertices;
        for (int i = 0; i < verts.Length; i++) {
            verts[i] = verts[i] - center;
        }
        vertexBuffer.SetData(verts);

        MtVImplementation.SetBuffer(MtV, "IndexBuffer", indicesBuffer);
        MtVImplementation.SetBuffer(MtV, "VertexBuffer", vertexBuffer);
        MtVImplementation.SetInt("tris", numTris);
        MtVImplementation.SetFloats("offset", offset.x, offset.y, offset.z);
        MtVImplementation.SetInt("numSamples", (int)numSamplesPerTriangle);
        MtVImplementation.SetVector("scale3d", scaleMeshBy);
        MtVImplementation.SetInt("voxelSide", (int)voxelResolution);

        if(voxels == null) {
            voxels = new RenderTexture(voxelResolution, voxelResolution,
                    0, RenderTextureFormat.ARGBHalf);
            voxels.dimension = UnityEngine.Rendering.TextureDimension.Tex3D;
            voxels.enableRandomWrite = true;
            voxels.useMipMap = false;
            voxels.volumeDepth = voxelResolution;
            voxels.Create();
        }


        MtVImplementation.SetBuffer(Zero, "IndexBuffer", indicesBuffer);
        MtVImplementation.SetBuffer(Zero, "VertexBuffer", vertexBuffer);
        MtVImplementation.SetTexture(Zero, "Voxels", voxels);
        MtVImplementation.Dispatch(Zero, numGroups(voxelResolution, 8),
            numGroups(voxelResolution, 8), numGroups(voxelResolution, 8));

        MtVImplementation.SetTexture(MtV, "Voxels", voxels);
        MtVImplementation.Dispatch(MtV, numGroups(numTris, 512), 1, 1);

        return voxels;
    }

    RenderTexture MakeDebugTexture() {
        var voxels = new RenderTexture(64, 64, 0, RenderTextureFormat.ARGBHalf);
        voxels.dimension = UnityEngine.Rendering.TextureDimension.Tex3D;
        voxels.enableRandomWrite = true;
        voxels.useMipMap = false;
        voxels.volumeDepth = 64;
        voxels.Create();
        int dispatchCubeSize = voxels.width;
        JFAImplementation.SetInt("dispatchCubeSide", dispatchCubeSize);
        JFAImplementation.SetTexture(DebugSphere, "Voxels", voxels);
        JFAImplementation.Dispatch(DebugSphere, numGroups(voxels.width, 8),
           numGroups(voxels.height, 8), numGroups(voxels.volumeDepth, 8));
        return voxels;
    }

    // number of groups for a dispatch with totalThreads and groups of size
    // numThreadsForDim
    int numGroups(int totalThreads, int groupSize) {
        return (totalThreads + (groupSize - 1)) / groupSize;
    }
}

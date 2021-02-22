﻿using System.Collections;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using RenderPipeline = UnityEngine.Rendering.RenderPipelineManager;
using UnityEngine.UIElements;

[ExecuteInEditMode]
public class Mirror : MonoBehaviour
{
    #region Variables
    // Public variables
    [Header("Main Settings")]
    public Vector3 projectionDirection = Vector3.forward;
    public LayerMask m_LayerMask = -1; // Set the layermask for the portal camera
    public int m_TextureSize = 1024; // The texture size (resolution)

    [Header("Advanced Settings")]
    //clipping & culling
    public float m_ClipPlaneOffset = 0.001f;
    public float nearClipLimit = 0.2f;
    // Texture settings
    public bool m_DisablePixelLights = true;
    public int m_framesNeededToUpdate = 0;


    // Private variables
    private Dictionary<Camera, Camera> m_PortalCameras = new Dictionary<Camera, Camera>();

    private int m_frameCounter = 0;
    private static bool s_InsideRendering = false; // To prevent recursion
    private List<XRNodeState> nodeStates = new List<XRNodeState>();

    private RenderTexture m_PortalTextureLeft = null;
    private RenderTexture m_PortalTextureRight = null;
    private int m_OldReflectionTextureSize = 0;

    #endregion

    #region Methods

    private void OnEnable()
    {
        RenderPipeline.beginCameraRendering += UpdateCamera;
    }

    private void OnDisable()
    {
        RenderPipeline.beginCameraRendering -= UpdateCamera;

        // Cleanup all the objects we possibly have created
        if (m_PortalTextureLeft)
        {
            DestroyImmediate(m_PortalTextureLeft);
            m_PortalTextureLeft = null;
        }
        if (m_PortalTextureRight)
        {
            DestroyImmediate(m_PortalTextureRight);
            m_PortalTextureRight = null;
        }
        foreach (var kvp in m_PortalCameras)
            DestroyImmediate(((Camera)kvp.Value).gameObject);

        m_PortalCameras.Clear();
    }

    #endregion

    #region Functions

    void UpdateCamera(ScriptableRenderContext SRC, Camera camera)
    {
        if ((camera.cameraType == CameraType.Game || camera.cameraType == CameraType.SceneView) &&
            camera.tag != "PortalCam") // is the current camera eligeble for portalling?
        {
            if (m_frameCounter > 0) // update over how many frames?
            {
                m_frameCounter--;
                return;
            }

            var rend = GetComponent<Renderer>();

            if (!enabled || !rend || !rend.sharedMaterial || !rend.enabled) // <<<< Why does the renderer NEED to have a shared material??
                return;

            // Safeguard from recursive reflections.  
            if (s_InsideRendering)
                return;
            s_InsideRendering = true;

            m_frameCounter = m_framesNeededToUpdate;
            
            // Render the camera
            RenderCamera(camera, rend, Camera.StereoscopicEye.Left, ref m_PortalTextureLeft, SRC);
            if (camera.stereoEnabled)
            {
                try
                {
                    RenderCamera(camera, rend, Camera.StereoscopicEye.Right, ref m_PortalTextureRight, SRC);
                }
                catch (Exception e)
                {
                    Debug.LogException(e, this);
                }
            }
        }
    }
    
    private void RenderCamera(Camera camera, Renderer rend, Camera.StereoscopicEye eye, ref RenderTexture portalTexture, ScriptableRenderContext SRC)
    {
        // Create the camera that will render the reflection
        Camera portalCamera;
        CreatePortalCamera(camera, eye, out portalCamera, ref portalTexture);
        CopyCameraProperties(camera, portalCamera); // Copy the properties of the (player) camera

        // find out the reflection plane: position and normal in world space
        Vector3 pos = transform.position; //portalRenderPlane.transform.forward;//
        Vector3 normal = transform.TransformDirection(projectionDirection); // Alex: This is done because sometimes the object reflection direction does not align with what was the default (transform.forward), in this way, the user can specify this.
        //normal.Normalize(); // Alex: normalize in case someone enters a non-normalized vector. Turned off for now because it is a fun effect :P

        // Optionally disable pixel lights for reflection
        int oldPixelLightCount = QualitySettings.pixelLightCount;
        if (m_DisablePixelLights)
            QualitySettings.pixelLightCount = 0;

        // Reflect camera around reflection plane
        float d = -Vector3.Dot(normal, pos) - m_ClipPlaneOffset;
        Vector4 reflectionPlane = new Vector4(normal.x, normal.y, normal.z, d);

        Matrix4x4 reflection = Matrix4x4.identity;
        CalculateReflectionMatrix(ref reflection, reflectionPlane);

        // Calculate the Eye offsets
        Vector3 oldEyePos;
        Matrix4x4 worldToCameraMatrix;
        if (camera.stereoEnabled)
        {
            Vector3 eyeOffset;
            worldToCameraMatrix = camera.GetStereoViewMatrix(eye);

            InputTracking.GetNodeStates(nodeStates);
            XRNodeState leftEyeState = findNode(nodeStates, XRNode.LeftEye);
            XRNodeState rightEyeState = findNode(nodeStates, XRNode.RightEye);

            if (eye == Camera.StereoscopicEye.Left)
                leftEyeState.TryGetPosition(out eyeOffset); //eyeOffset = InputTracking.GetLocalPosition(XRNode.LeftEye); //<< Deprecated
            else
                rightEyeState.TryGetPosition(out eyeOffset); //eyeOffset = InputTracking.GetLocalPosition(XRNode.RightEye); //<< Deprecated

            eyeOffset.z = 0.0f;
            oldEyePos = camera.transform.position + camera.transform.TransformVector(eyeOffset);
        }
        else
        {
            worldToCameraMatrix = camera.worldToCameraMatrix;
            oldEyePos = camera.transform.position;
        }

        // >>>Transform Camera<<<
        portalCamera.projectionMatrix = camera.projectionMatrix; // Match matrices <<<
        Vector3 newEyePos = reflection.MultiplyPoint(oldEyePos);
        portalCamera.transform.position = newEyePos;

        portalCamera.worldToCameraMatrix = worldToCameraMatrix * reflection;



        // Setup oblique projection matrix so that near plane is our reflection plane. This way we clip everything below/above it for free.
        Vector4 clipPlane = CameraSpacePlane(worldToCameraMatrix * reflection, pos, normal, 1.0f);

        Matrix4x4 projectionMatrix;

        if (camera.stereoEnabled)
            projectionMatrix = camera.GetStereoProjectionMatrix(eye);
        else
            projectionMatrix = camera.projectionMatrix;

        MakeProjectionMatrixOblique(ref projectionMatrix, clipPlane);

        portalCamera.projectionMatrix = projectionMatrix;
        portalCamera.cullingMask = m_LayerMask.value; // Set culling mask <<<<
        portalCamera.targetTexture = portalTexture; // Set the target texture <<<

        GL.invertCulling = true;

        portalCamera.transform.rotation = camera.transform.rotation;

        UniversalRenderPipeline.RenderSingleCamera(SRC, portalCamera); // URP Version of: portalCamera.Render();

        GL.invertCulling = false;

        // Assign the rendertexture to the material
        Material[] materials = rend.sharedMaterials; // Why only get the shared materials?
        string property = "_ReflectionTex" + eye.ToString();

        foreach (Material mat in materials)
        {
            if (mat.HasProperty(property))
                mat.SetTexture(property, portalTexture);
        }

        // Restore pixel light count
        if (m_DisablePixelLights)
            QualitySettings.pixelLightCount = oldPixelLightCount;

        s_InsideRendering = false;
    }

    private void CreatePortalCamera(Camera currentCamera, Camera.StereoscopicEye eye, out Camera portalCamera, ref RenderTexture portalTexture)
    {
        portalCamera = null;

        // Create the render texture (if needed)
        if (!portalTexture || m_OldReflectionTextureSize != m_TextureSize) // if it doesn't exist or the size has changed
        {
            if (portalTexture) // if it does exist
                DestroyImmediate(portalTexture); // destroy it first

            portalTexture = new RenderTexture(m_TextureSize, m_TextureSize, 24); // <<<< make buffer size 24??
            portalTexture.name = "__MirrorReflection" + eye.ToString() + GetInstanceID(); // create the name of the object
            portalTexture.isPowerOfTwo = true; // https://docs.unity3d.com/Manual/Textures.html: Non power of two texture assets can be scaled up at import time using the Non Power of 2 option in the advanced texture type in the import settings. Unity will scale texture contents as requested, and in the game they will behave just like any other texture, so they can still be compressed and very fast to load.
            portalTexture.hideFlags = HideFlags.DontSave; // The object will not be saved to the Scene. It will not be destroyed when a new Scene is loaded.

            portalTexture.antiAliasing = 4; // < <<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<<ResourceIntensive but pretty

            m_OldReflectionTextureSize = m_TextureSize; // save the old texture size
        }

        // Create camera with the render texture
        if (!m_PortalCameras.TryGetValue(currentCamera, out portalCamera)) // if it does not yet exist in the dictionary, create it. If it does, assign it. (catch both not-in-dictionary and in-dictionary-but-deleted-GO)
        {
            GameObject go = new GameObject("Mirror Reflection Camera id" + GetInstanceID() + " for " + currentCamera.GetInstanceID(), typeof(Camera), typeof(Skybox)); // create the new game object
            portalCamera = go.GetComponent<Camera>();
            portalCamera.enabled = false;
            portalCamera.transform.position = transform.position;
            portalCamera.transform.rotation = transform.rotation;
            portalCamera.tag = "PortalCam"; // Tag it as a portal camera so it doesn't participate in the additional CameraRender function
            //portalCamera.gameObject.AddComponent<FlareLayer>(); // Adds a flare layer to make Lens Flares appear in the image?? disabled for now
            go.hideFlags = HideFlags.DontSave; // The object will not be saved to the Scene. It will not be destroyed when a new Scene is loaded.
            m_PortalCameras.Add(currentCamera, portalCamera); // add the newly created camera to the dictionary
        }
    }

    private void CopyCameraProperties(Camera src, Camera dest)
    {
        if (dest == null) // to prevent errors
            return;

        // set camera to clear the same way as current camera <<< Not really sure what this does, more info: https://docs.unity3d.com/Manual/class-Camera.html
        dest.clearFlags = src.clearFlags;
        dest.backgroundColor = src.backgroundColor;

        if (src.clearFlags == CameraClearFlags.Skybox)
        {
            Skybox sky = src.GetComponent(typeof(Skybox)) as Skybox;
            Skybox mysky = dest.GetComponent(typeof(Skybox)) as Skybox;
            if (!sky || !sky.material)
            {
                mysky.enabled = false;
            }
            else
            {
                mysky.enabled = true;
                mysky.material = sky.material;
            }
        }

        // update other values to match current camera.
        // even if we are supplying custom camera&projection matrices,
        // some of values are used elsewhere (e.g. skybox uses far plane)
        dest.stereoTargetEye = StereoTargetEyeMask.None; // To prevent the camera from following some eye, else this gets fuckey sometimes (e.g. the FOV cant be copied)
        dest.farClipPlane = 30;// src.farClipPlane;// 30m is enough in this scene
        dest.nearClipPlane = src.nearClipPlane; 
        dest.orthographic = src.orthographic;
        dest.fieldOfView = src.fieldOfView;
        dest.aspect = src.aspect;
        dest.orthographicSize = src.orthographicSize;
        dest.depth = 2;
        dest.GetUniversalAdditionalCameraData().renderPostProcessing = true;
    }

    // Given position/normal of the plane, calculates plane in camera space.
    private Vector4 CameraSpacePlane(Matrix4x4 worldToCameraMatrix, Vector3 pos, Vector3 normal, float sideSign)
    {
        Vector3 offsetPos = pos + normal * m_ClipPlaneOffset;
        Vector3 cpos = worldToCameraMatrix.MultiplyPoint(offsetPos);
        Vector3 cnormal = worldToCameraMatrix.MultiplyVector(normal).normalized * sideSign;
        return new Vector4(cnormal.x, cnormal.y, cnormal.z, -Vector3.Dot(cpos, cnormal));
    }

#endregion

    #region HelperMethods
    // Alex:
    XRNodeState findNode(List<XRNodeState> nodeStates, XRNode node)
    {
        XRNodeState nodeState = new XRNodeState();

        if (nodeStates.Count > 0)
        {
            nodeState = nodeStates[0];
            foreach (var node_i in nodeStates)
            {
                if (node_i.nodeType == XRNode.LeftEye)
                {
                    nodeState = node_i;
                    break;
                }
            }
        }


        return nodeState;
    }

    // Calculates reflection matrix around the given plane
    private static void CalculateReflectionMatrix(ref Matrix4x4 reflectionMat, Vector4 plane)
    {
        reflectionMat.m00 = (1F - 2F * plane[0] * plane[0]);
        reflectionMat.m01 = (-2F * plane[0] * plane[1]);
        reflectionMat.m02 = (-2F * plane[0] * plane[2]);
        reflectionMat.m03 = (-2F * plane[3] * plane[0]);

        reflectionMat.m10 = (-2F * plane[1] * plane[0]);
        reflectionMat.m11 = (1F - 2F * plane[1] * plane[1]);
        reflectionMat.m12 = (-2F * plane[1] * plane[2]);
        reflectionMat.m13 = (-2F * plane[3] * plane[1]);

        reflectionMat.m20 = (-2F * plane[2] * plane[0]);
        reflectionMat.m21 = (-2F * plane[2] * plane[1]);
        reflectionMat.m22 = (1F - 2F * plane[2] * plane[2]);
        reflectionMat.m23 = (-2F * plane[3] * plane[2]);

        reflectionMat.m30 = 0F;
        reflectionMat.m31 = 0F;
        reflectionMat.m32 = 0F;
        reflectionMat.m33 = 1F;
    }

    // Extended sign: returns -1, 0 or 1 based on sign of a
    private static float sgn(float a)
    {
        if (a > 0.0f) return 1.0f;
        if (a < 0.0f) return -1.0f;
        return 0.0f;
    }

    // taken from http://www.terathon.com/code/oblique.html
    private static void MakeProjectionMatrixOblique(ref Matrix4x4 matrix, Vector4 clipPlane)
    {
        Vector4 q;

        // Calculate the clip-space corner point opposite the clipping plane
        // as (sgn(clipPlane.x), sgn(clipPlane.y), 1, 1) and
        // transform it into camera space by multiplying it
        // by the inverse of the projection matrix

        q.x = (sgn(clipPlane.x) + matrix[8]) / matrix[0];
        q.y = (sgn(clipPlane.y) + matrix[9]) / matrix[5];
        q.z = -1.0F;
        q.w = (1.0F + matrix[10]) / matrix[14];

        // Calculate the scaled plane vector
        Vector4 c = clipPlane * (2.0F / Vector3.Dot(clipPlane, q));

        // Replace the third row of the projection matrix
        matrix[2] = c.x;
        matrix[6] = c.y;
        matrix[10] = c.z + 1.0F;
        matrix[14] = c.w;
    }
    #endregion
}
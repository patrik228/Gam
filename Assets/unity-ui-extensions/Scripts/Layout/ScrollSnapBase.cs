﻿using System;
using UnityEngine.Events;
using UnityEngine.EventSystems;

namespace UnityEngine.UI.Extensions
{
    public class ScrollSnapBase : MonoBehaviour, IBeginDragHandler, IDragHandler
    {
        internal RectTransform _screensContainer;
        internal bool _isVertical;

        internal int _screens = 1;

        internal float _scrollStartPosition;
        internal float _childSize;
        private float _childPos, _maskSize;
        internal Vector2 _childAnchorPoint;
        internal ScrollRect _scroll_rect;
        internal Vector3 _lerp_target;
        internal bool _lerp;
        internal bool _pointerDown = false;
        internal bool _settled = true;
        internal Vector3 _startPosition = new Vector3();
        [Tooltip("The currently active page")]
        internal int _currentPage;
        internal int _previousPage;
        internal int _halfNoVisibleItems;
        private int _bottomItem, _topItem;

        [Serializable]
        public class SelectionChangeStartEvent : UnityEvent { }
        [Serializable]
        public class SelectionPageChangedEvent : UnityEvent<int> { }
        [Serializable]
        public class SelectionChangeEndEvent : UnityEvent<int> { }

        [Tooltip("The screen / page to start the control on\n*Note, this is a 0 indexed array")]
        [SerializeField]
        public int StartingScreen = 0;

        [Tooltip("The distance between two pages based on page height, by default pages are next to each other")]
        [SerializeField]
        [Range(1, 8)]
        public float PageStep = 1;

        [Tooltip("The gameobject that contains toggles which suggest pagination. (optional)")]
        public GameObject Pagination;

        [Tooltip("Button to go to the next page. (optional)")]
        public GameObject NextButton;

        [Tooltip("Button to go to the previous page. (optional)")]
        public GameObject PrevButton;

        [Tooltip("Transition speed between pages. (optional)")]
        public float transitionSpeed = 7.5f;

        [Tooltip("Fast Swipe makes swiping page next / previous (optional)")]
        public Boolean UseFastSwipe = false;

        [Tooltip("How far swipe has to travel to initiate a page change (optional)")]
        public int FastSwipeThreshold = 100;

        [Tooltip("Speed at which the ScrollRect will keep scrolling before slowing down and stopping (optional)")]
        public int SwipeVelocityThreshold = 200;

        [Tooltip("The visible bounds area, controls which items are visible/enabled. *Note Should use a RectMask. (optional)")]
        public RectTransform MaskArea;

        [Tooltip("Pixel size to buffer arround Mask Area. (optional)")]
        public float MaskBuffer = 1;
        
        public int CurrentPage
        {
            get
            {
                return _currentPage;
            }
            internal set
            {
                if ((value != _currentPage && value >= 0 && value < _screensContainer.childCount) || (value == 0 && _screensContainer.childCount == 0))
                {
                    _previousPage = _currentPage;
                    _currentPage = value;
                    if(MaskArea) UpdateVisible();
                    if(!_lerp) ScreenChange();
                    ChangeBulletsInfo(_currentPage);
                }
            }
        }

        [Tooltip("(Experimental)\nBy default, child array objects will use the parent transform\nHowever you can disable this for some interesting effects")]
        public bool UseParentTransform = true;

        [Tooltip("Scroll Snap children. (optional)\nEither place objects in the scene as children OR\nPrefabs in this array, NOT BOTH")]
        public GameObject[] ChildObjects;

        public bool ignoreTimeScale;
        
        [SerializeField]
        [Tooltip("Event fires when a user starts to change the selection")]
        private SelectionChangeStartEvent m_OnSelectionChangeStartEvent = new SelectionChangeStartEvent();
        public SelectionChangeStartEvent OnSelectionChangeStartEvent { get { return m_OnSelectionChangeStartEvent; } set { m_OnSelectionChangeStartEvent = value; } }

        [SerializeField]
        [Tooltip("Event fires as the page changes, while dragging or jumping")]
        private SelectionPageChangedEvent m_OnSelectionPageChangedEvent = new SelectionPageChangedEvent();
        public SelectionPageChangedEvent OnSelectionPageChangedEvent { get { return m_OnSelectionPageChangedEvent; } set { m_OnSelectionPageChangedEvent = value; } }

        [SerializeField]
        [Tooltip("Event fires when the page settles after a user has dragged")]
        private SelectionChangeEndEvent m_OnSelectionChangeEndEvent = new SelectionChangeEndEvent();
        public SelectionChangeEndEvent OnSelectionChangeEndEvent { get { return m_OnSelectionChangeEndEvent; } set { m_OnSelectionChangeEndEvent = value; } }


        // Use this for initialization
        void Awake()
        {
            _scroll_rect = gameObject.GetComponent<ScrollRect>();

            if (_scroll_rect.horizontalScrollbar || _scroll_rect.verticalScrollbar)
            {
                Debug.LogWarning("Warning, using scrollbars with the Scroll Snap controls is not advised as it causes unpredictable results");
            }

            if (StartingScreen < 0)
            {
                StartingScreen = 0;
            }

            _screensContainer = _scroll_rect.content;
            if (ChildObjects != null && ChildObjects.Length > 0)
            {
                if (_screensContainer.transform.childCount > 0)
                {
                    Debug.LogError("ScrollRect Content has children, this is not supported when using managed Child Objects\n Either remove the ScrollRect Content children or clear the ChildObjects array");
                    return;
                }
                InitialiseChildObjectsFromArray();
            }
            else
            {
                InitialiseChildObjectsFromScene();
            }

            if (NextButton)
                NextButton.GetComponent<Button>().onClick.AddListener(() => { NextScreen(); });

            if (PrevButton)
                PrevButton.GetComponent<Button>().onClick.AddListener(() => { PreviousScreen(); });
        }

        internal void InitialiseChildObjectsFromScene()
        {
            int childCount = _screensContainer.childCount;
            ChildObjects = new GameObject[childCount];
            for (int i = 0; i < childCount; i++)
            {
                ChildObjects[i] = _screensContainer.transform.GetChild(i).gameObject;
                if (MaskArea && ChildObjects[i].activeSelf)
                {
                    ChildObjects[i].SetActive(false);
                }
            }
        }

        internal void InitialiseChildObjectsFromArray()
        {
            int childCount = ChildObjects.Length;
            RectTransform childRect;
            GameObject child;
            for (int i = 0; i < childCount; i++)
            {
                child = GameObject.Instantiate(ChildObjects[i]);
                //Optionally, use original GO transform when initialising, by default will use parent RectTransform position/rotation
                if (UseParentTransform)
                {
                    childRect = child.GetComponent<RectTransform>();
                    childRect.rotation = _screensContainer.rotation;
                    childRect.localScale = _screensContainer.localScale;
                    childRect.position = _screensContainer.position;
                }
                child.transform.SetParent(_screensContainer.transform);
                ChildObjects[i] = child;
                if (MaskArea && ChildObjects[i].activeSelf)
                {
                    ChildObjects[i].SetActive(false);
                }
            }
        }

        internal void UpdateVisible()
        {
            //If there are no objects in the scene or a mask, exit
            if (!MaskArea || ChildObjects == null || ChildObjects.Length < 1 || _screensContainer.childCount < 1)
            {
                return;
            }

            _maskSize = _isVertical ? MaskArea.rect.height : MaskArea.rect.width;
            _halfNoVisibleItems = (int)Math.Round(_maskSize / (_childSize * MaskBuffer), MidpointRounding.AwayFromZero) / 2;
            _bottomItem = _topItem = 0;
            //work out how many items below the current page can be visible
            for (int i = _halfNoVisibleItems + 1; i > 0 ; i--)
            {
                _bottomItem = _currentPage - i < 0 ? 0 : i;
                if (_bottomItem > 0) break;
            }
            //work out how many items above the current page can be visible
            for (int i = _halfNoVisibleItems + 1; i > 0; i--)
            {
                _topItem = _screensContainer.childCount - _currentPage - i < 0 ? 0 : i;
                if (_topItem > 0) break;
            }
            //Set the active items active
            for (int i = CurrentPage - _bottomItem; i < CurrentPage + _topItem; i++)
            {
                try
                {
                    ChildObjects[i].SetActive(true);
                }
                catch
                {
                    Debug.Log("Failed to setactive child [" + i + "]");
                }
            }

            //Deactivate items out of visibility at the bottom of the ScrollRect Mask (only on scroll)
            if (_currentPage > _halfNoVisibleItems) ChildObjects[CurrentPage - _bottomItem].SetActive(false);
            //Deactivate items out of visibility at the top of the ScrollRect Mask (only on scroll)
            if (_screensContainer.childCount - _currentPage > _topItem) ChildObjects[CurrentPage + _topItem].SetActive(false);
        }


        //Function for switching screens with buttons
        public void NextScreen()
        {
            if (_currentPage < _screens - 1)
            {
                if (!_lerp) StartScreenChange();

                _lerp = true;
                CurrentPage = _currentPage + 1;
                GetPositionforPage(_currentPage, ref _lerp_target);
                ScreenChange();
            }
        }

        //Function for switching screens with buttons
        public void PreviousScreen()
        {
            if (_currentPage > 0)
            {
                if (!_lerp) StartScreenChange();

                _lerp = true;
                CurrentPage = _currentPage - 1;
                GetPositionforPage(_currentPage, ref _lerp_target);
                ScreenChange();
            }
        }

        /// <summary>
        /// Function for switching to a specific screen
        /// *Note, this is based on a 0 starting index - 0 to x
        /// </summary>
        /// <param name="screenIndex">0 starting index of page to jump to</param>
        public void GoToScreen(int screenIndex)
        {
            if (screenIndex <= _screens - 1 && screenIndex >= 0)
            {
                if (!_lerp) StartScreenChange();

                _lerp = true;
                CurrentPage = screenIndex;
                GetPositionforPage(_currentPage, ref _lerp_target);
                ScreenChange();
            }
        }

        /// <summary>
        /// Gets the closest page for the current Scroll Rect container position
        /// </summary>
        /// <param name="pos">Position to test, normally the Scroll Rect container Local position</param>
        /// <returns>Closest Page number (zero indexed array value)</returns>
        internal int GetPageforPosition(Vector3 pos)
        {
            return _isVertical ? 
                -(int)Math.Round((pos.y - _scrollStartPosition) / _childSize) :
                -(int)Math.Round((pos.x - _scrollStartPosition) / _childSize);
        }

        /// <summary>
        /// Validates if the current Scroll Rect container position is within the bounds for a page
        /// </summary>
        /// <param name="pos">Position to test, normally the Scroll Rect container Local position</param>
        /// <returns>True / False, is the position in the bounds of a page</returns>
        internal bool IsRectSettledOnaPage(Vector3 pos)
        {
            return _isVertical ?
                -((pos.y - _scrollStartPosition) / _childSize) == -(int)Math.Round((pos.y - _scrollStartPosition) / _childSize) :
                -((pos.x - _scrollStartPosition) / _childSize)  == -(int)Math.Round((pos.x - _scrollStartPosition) / _childSize);
        }

        /// <summary>
        /// Returns the local position for a child page based on the required page number
        /// </summary>
        /// <param name="page">Page that the position is required for (Zero indexed array value)</param>
        /// <param name="target">Outputs the local position for the selected page</param>
        internal void GetPositionforPage(int page, ref Vector3 target)
        {
            _childPos = -_childSize * page;
            if (_isVertical)
            {
                target.y = _childPos + _scrollStartPosition;
            }
            else
            {
                target.x = _childPos + _scrollStartPosition;
            }
        }

        /// <summary>
        /// Updates the _Lerp target to the closest page and updates the pagination bullets.  Each control's update loop will then handle the move.
        /// </summary>
        internal void ScrollToClosestElement()
        {
            _lerp = true;
            CurrentPage = GetPageforPosition(_screensContainer.localPosition);
            GetPositionforPage(_currentPage, ref _lerp_target);
            ChangeBulletsInfo(_currentPage);
        }
        /// <summary>
        /// changes the bullets on the bottom of the page - pagination
        /// </summary>
        /// <param name="targetScreen"></param>
        internal void ChangeBulletsInfo(int targetScreen)
        {
            if (Pagination)
                for (int i = 0; i < Pagination.transform.childCount; i++)
                {
                    Pagination.transform.GetChild(i).GetComponent<Toggle>().isOn = (targetScreen == i)
                        ? true
                        : false;
                }
        }

        private void OnValidate()
        {
            var children  = gameObject.GetComponent<ScrollRect>().content.childCount;
            if (children != 0 || ChildObjects != null)
            {
                var childCount = ChildObjects == null || ChildObjects.Length == 0 ? children : ChildObjects.Length;
                if (StartingScreen > childCount - 1)
                {
                    StartingScreen = childCount - 1;
                }
                if (StartingScreen < 0)
                {
                    StartingScreen = 0;
                }
            }
            if (MaskBuffer <= 0)
            {
                MaskBuffer = 1;
            }
            if (PageStep < 0)
            {
                PageStep = 0;
            }
            if (PageStep > 8)
            {
                PageStep = 9;
            }
        }

        /// <summary>
        /// Event fires when the user starts to change the page, either via swipe or button.
        /// </summary>
        internal void StartScreenChange()
        {
            OnSelectionChangeStartEvent.Invoke();
        }

        /// <summary>
        /// Event fires when the currently viewed page changes, also updates while the scroll is moving
        /// </summary>
        internal void ScreenChange()
        {
            OnSelectionPageChangedEvent.Invoke(_currentPage);
        }

        /// <summary>
        /// Event fires when control settles on a page, outputs the new page number
        /// </summary>
        internal void EndScreenChange()
        {
            OnSelectionChangeEndEvent.Invoke(_currentPage);
            _settled = true;
        }

        #region Interfaces
        /// <summary>
        /// Touch screen to start swiping
        /// </summary>
        /// <param name="eventData"></param>
        public void OnBeginDrag(PointerEventData eventData)
        {
            _pointerDown = true;
            _settled = false;
            StartScreenChange();
            _startPosition = _screensContainer.localPosition;
        }

        /// <summary>
        /// While dragging do
        /// </summary>
        /// <param name="eventData"></param>
        public void OnDrag(PointerEventData eventData)
        {
            _lerp = false;
        }

        #endregion
    }
}
import PropTypes from 'prop-types';
import React, { Component } from 'react';
import BookCover from 'Book/BookCover';
import Alert from 'Components/Alert';
import Label from 'Components/Label';
import Button from 'Components/Link/Button';
import Link from 'Components/Link/Link';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import RelativeDateCellConnector from 'Components/Table/Cells/RelativeDateCellConnector';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TablePager from 'Components/Table/TablePager';
import TableRow from 'Components/Table/TableRow';
import { kinds } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import translate from 'Utilities/String/translate';
import styles from './ImportListItemsModalContent.css';

const PAGE_SIZE = 20;

const STATES = {
  downloaded: { kind: kinds.SUCCESS, label: 'Downloaded' },
  queued: { kind: kinds.PURPLE, label: 'Queued' },
  missing: { kind: kinds.WARNING, label: 'Missing' },
  unmonitored: { kind: kinds.DISABLED, label: 'Unmonitored' },
  excluded: { kind: kinds.DANGER, label: 'Excluded' },
  pendingAuthor: { kind: kinds.INFO, label: 'PendingAuthor' },
  notInLibrary: { kind: kinds.DEFAULT, label: 'NotInLibrary' }
};

function mediaTypeLabel(mediaType) {
  return mediaType === 'ebook' ? translate('Ebook') : translate('Audiobook');
}

function renderDateCell(date, caption) {
  return (
    <TableRowCell className={styles.dateCell}>
      {
        date ?
          <RelativeDateCellConnector
            className={styles.dateValue}
            date={date}
            component="span"
          /> :
          <Label
            kind={kinds.DEFAULT}
            outline={true}
          >
            {translate('NotAvailable')}
          </Label>
      }

      {
        date && caption ?
          <div className={styles.dateSource}>
            {caption}
          </div> :
          null
      }
    </TableRowCell>
  );
}

class ImportListItemsModalContent extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      page: 1,
      totalRecords: 0,
      records: [],
      isFetching: true,
      error: null
    };

    this._request = null;
  }

  componentDidMount() {
    this.fetchItems(1, false);
  }

  componentWillUnmount() {
    if (this._request) {
      this._request.abortRequest();
      this._request = null;
    }
  }

  //
  // Control

  fetchItems(page, refresh) {
    if (this._request) {
      this._request.abortRequest();
    }

    this.setState({ isFetching: true, error: null });

    const { id } = this.props;

    const ajaxRequest = createAjaxRequest({
      url: id ? `/importlist/${id}/items` : '/importlist/items',
      dataType: 'json',
      data: { page, pageSize: PAGE_SIZE, refresh }
    });

    this._request = ajaxRequest;

    ajaxRequest.request.then((data) => {
      if (this._request !== ajaxRequest) {
        return;
      }

      this._request = null;

      this.setState({
        page: data.page,
        totalRecords: data.totalRecords,
        records: data.records,
        isFetching: false
      });
    }, (xhr) => {
      if (xhr.aborted || this._request !== ajaxRequest) {
        return;
      }

      this._request = null;

      this.setState({
        isFetching: false,
        error: xhr
      });
    });
  }

  getTotalPages() {
    return Math.max(1, Math.ceil(this.state.totalRecords / PAGE_SIZE));
  }

  //
  // Listeners

  onFirstPagePress = () => {
    this.fetchItems(1, false);
  };

  onPreviousPagePress = () => {
    this.fetchItems(Math.max(1, this.state.page - 1), false);
  };

  onNextPagePress = () => {
    this.fetchItems(Math.min(this.getTotalPages(), this.state.page + 1), false);
  };

  onLastPagePress = () => {
    this.fetchItems(this.getTotalPages(), false);
  };

  onPageSelect = (page) => {
    this.fetchItems(page, false);
  };

  onRefreshPress = () => {
    this.fetchItems(this.state.page, true);
  };

  //
  // Render

  renderStates({ state, media, pendingError }) {
    if (state === 'excluded' || !media.length) {
      return (
        <Label
          kind={STATES[state].kind}
          outline={true}
          title={pendingError}
        >
          {translate(STATES[state].label)}
        </Label>
      );
    }

    return media.map((entry) => (
      <Label
        key={entry.bookId}
        kind={STATES[entry.state].kind}
        outline={entry.state !== 'downloaded'}
      >
        {`${mediaTypeLabel(entry.mediaType)}: ${translate(STATES[entry.state].label)}`}
      </Label>
    ));
  }

  renderRow(item, index) {
    const {
      title,
      subtitle,
      author,
      images,
      bookId,
      media,
      sources,
      releaseDate,
      dateAdded,
      dateAddedFromList
    } = item;

    const bookLink = bookId ? `/book/${bookId}` : null;

    const cover = (
      <BookCover
        className={styles.cover}
        images={images}
        size={70}
        lazy={false}
        overflow={true}
      />
    );

    return (
      <TableRow key={`${bookId || title}-${index}`}>
        <TableRowCell className={styles.coverCell}>
          {
            bookLink ?
              <Link
                className={styles.coverLink}
                to={bookLink}
                title={translate('ViewBook')}
              >
                {cover}
              </Link> :
              cover
          }
        </TableRowCell>

        <TableRowCell className={styles.titleCell}>
          <div className={styles.title}>
            {
              bookLink ?
                <Link
                  to={bookLink}
                  title={translate('ViewBook')}
                >
                  {title}
                </Link> :
                title
            }
          </div>

          {
            subtitle ?
              <div className={styles.subtitle}>{subtitle}</div> :
              null
          }

          {
            author ?
              <div className={styles.author}>{author}</div> :
              null
          }

          {
            media.length ?
              <div className={styles.mediaLinks}>
                {
                  media.map((entry, linkIndex) => (
                    <span key={entry.bookId}>
                      {linkIndex > 0 ? ' · ' : null}
                      <Link to={`/book/${entry.bookId}`}>
                        {mediaTypeLabel(entry.mediaType)}
                      </Link>
                    </span>
                  ))
                }
              </div> :
              null
          }
        </TableRowCell>

        {
          this.props.id ?
            null :
            <TableRowCell className={styles.sourcesCell}>
              {
                sources.map((source) => (
                  <div
                    key={source.importListId}
                    className={styles.source}
                  >
                    <div className={styles.sourceType}>{source.implementationName}</div>
                    <div className={styles.sourceName}>{source.name}</div>
                  </div>
                ))
              }
            </TableRowCell>
        }

        <TableRowCell className={styles.statusCell}>
          <div className={styles.states}>
            {this.renderStates(item)}
          </div>
        </TableRowCell>

        {renderDateCell(releaseDate)}
        {renderDateCell(dateAdded, dateAddedFromList ? null : translate('InLibrarySince'))}
      </TableRow>
    );
  }

  render() {
    const {
      id,
      name,
      onModalClose
    } = this.props;

    const {
      page,
      totalRecords,
      records,
      isFetching,
      error
    } = this.state;

    const columns = [
      {
        name: 'cover',
        label: '',
        isVisible: true
      },
      {
        name: 'title',
        label: translate('Title'),
        isVisible: true
      },
      {
        name: 'sources',
        className: styles.leftHeader,
        label: translate('ImportLists'),
        isVisible: !id
      },
      {
        name: 'status',
        className: styles.leftHeader,
        label: translate('Status'),
        isVisible: true
      },
      {
        name: 'releaseDate',
        className: styles.leftHeader,
        label: translate('ReleaseDate'),
        isVisible: true
      },
      {
        name: 'dateAdded',
        className: styles.leftHeader,
        label: translate('DateAdded'),
        isVisible: true
      }
    ];

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {id ? translate('ImportListItemsHeader', { name }) : translate('AllImportListItemsHeader')}
        </ModalHeader>

        <ModalBody>
          {
            isFetching && !records.length ?
              <LoadingIndicator /> :
              null
          }

          {
            error ?
              <Alert kind={kinds.DANGER}>
                {translate('ImportListItemsLoadError')}
              </Alert> :
              null
          }

          {
            !isFetching && !error && !records.length ?
              <div className={styles.empty}>
                {translate('ImportListItemsEmpty')}
              </div> :
              null
          }

          {
            records.length ?
              <div>
                <Table columns={columns}>
                  <TableBody>
                    {records.map((item, index) => this.renderRow(item, index))}
                  </TableBody>
                </Table>

                <TablePager
                  page={page}
                  totalPages={this.getTotalPages()}
                  totalRecords={totalRecords}
                  isFetching={isFetching}
                  onFirstPagePress={this.onFirstPagePress}
                  onPreviousPagePress={this.onPreviousPagePress}
                  onNextPagePress={this.onNextPagePress}
                  onLastPagePress={this.onLastPagePress}
                  onPageSelect={this.onPageSelect}
                />
              </div> :
              null
          }
        </ModalBody>

        <ModalFooter>
          <Button
            isDisabled={isFetching}
            onPress={this.onRefreshPress}
          >
            {translate('Refresh')}
          </Button>

          <Button onPress={onModalClose}>
            {translate('Close')}
          </Button>
        </ModalFooter>
      </ModalContent>
    );
  }
}

ImportListItemsModalContent.propTypes = {
  id: PropTypes.number,
  name: PropTypes.string,
  onModalClose: PropTypes.func.isRequired
};

export default ImportListItemsModalContent;
